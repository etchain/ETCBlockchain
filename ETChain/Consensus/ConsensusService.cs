﻿using AntShares.Core;
using AntShares.Cryptography;
using AntShares.IO;
using AntShares.Network;
using AntShares.Network.Payloads;
using AntShares.Wallets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AntShares.Consensus
{
    public class ConsensusService : IDisposable
    {
        public const int MaxTransactionsPerBlock = 15000;

        private ConsensusContext context = new ConsensusContext();
        private LocalNode localNode;
        private Wallet wallet;
        private Timer timer;
        private uint timer_height;
        private byte timer_view;
        private DateTime block_received_time;
        private bool started = false;

        public ConsensusService(LocalNode localNode, Wallet wallet)
        {
            this.localNode = localNode;
            this.wallet = wallet;
            this.timer = new Timer(OnTimeout, null, Timeout.Infinite, Timeout.Infinite);
        }

        private bool AddTransaction(Transaction tx, bool verify)
        {
            if (Blockchain.Default.ContainsTransaction(tx.Hash) ||
                (verify && !tx.Verify(context.Transactions.Values)) ||
                !CheckPolicy(tx))
            {
                Log($"reject tx: {tx.Hash}{Environment.NewLine}{tx.ToArray().ToHexString()}");
                RequestChangeView();
                return false;
            }
            context.Transactions[tx.Hash] = tx;
            if (context.TransactionHashes.Length == context.Transactions.Count)
            {
                if (Blockchain.GetConsensusAddress(Blockchain.Default.GetValidators(context.Transactions.Values).ToArray()).Equals(context.NextConsensus))
                {
                    Log($"send perpare response");
                    context.State |= ConsensusState.SignatureSent;
                    context.Signatures[context.MyIndex] = context.MakeHeader().Sign(wallet.GetKey(context.Validators[context.MyIndex]));
                    SignAndRelay(context.MakePerpareResponse(context.Signatures[context.MyIndex]));
                    CheckSignatures();
                }
                else
                {
                    RequestChangeView();
                    return false;
                }
            }
            return true;
        }

        private void Blockchain_PersistCompleted(object sender, Block block)
        {
            Log($"persist block: {block.Hash}");
            block_received_time = DateTime.Now;
            InitializeConsensus(0);
        }

        private void CheckExpectedView(byte view_number)
        {
            if (context.ViewNumber == view_number) return;
            if (context.ExpectedView.Count(p => p == view_number) >= context.M)
            {
                InitializeConsensus(view_number);
            }
        }

        protected virtual bool CheckPolicy(Transaction tx)
        {
            return true;
        }

        private void CheckSignatures()
        {
            if (context.Signatures.Count(p => p != null) >= context.M && context.TransactionHashes.All(p => context.Transactions.ContainsKey(p)))
            {
                Contract contract = Contract.CreateMultiSigContract(context.Validators[context.MyIndex].EncodePoint(true).ToScriptHash(), context.M, context.Validators);
                Block block = context.MakeHeader();
                SignatureContext sc = new SignatureContext(block);
                for (int i = 0, j = 0; i < context.Validators.Length && j < context.M; i++)
                    if (context.Signatures[i] != null)
                    {
                        sc.AddSignature(contract, context.Validators[i], context.Signatures[i]);
                        j++;
                    }
                sc.Verifiable.Scripts = sc.GetScripts();
                block.Transactions = context.TransactionHashes.Select(p => context.Transactions[p]).ToArray();
                Log($"relay block: {block.Hash}");
                if (!localNode.Relay(block))
                    Log($"reject block: {block.Hash}");
                context.State |= ConsensusState.BlockSent;
            }
        }

        private MinerTransaction CreateMinerTransaction(IEnumerable<Transaction> transactions, uint height, ulong nonce)
        {
            Fixed8 amount_netfee = Block.CalculateNetFee(transactions);
            TransactionOutput[] outputs = amount_netfee == Fixed8.Zero ? new TransactionOutput[0] : new[] { new TransactionOutput
            {
                AssetId = Blockchain.SystemCoin.Hash,
                Value = amount_netfee,
                ScriptHash = wallet.GetContracts().First().ScriptHash
            } };
            return new MinerTransaction
            {
                Nonce = (uint)(nonce % (uint.MaxValue + 1ul)),
                Attributes = new TransactionAttribute[0],
                Inputs = new CoinReference[0],
                Outputs = outputs,
                Scripts = new Witness[0]
            };
        }

        public void Dispose()
        {
            Log("OnStop");
            if (timer != null) timer.Dispose();
            if (started)
            {
                Blockchain.PersistCompleted -= Blockchain_PersistCompleted;
                LocalNode.InventoryReceiving -= LocalNode_InventoryReceiving;
                LocalNode.InventoryReceived -= LocalNode_InventoryReceived;
            }
        }

        private static ulong GetNonce()
        {
            byte[] nonce = new byte[sizeof(ulong)];
            Random rand = new Random();
            rand.NextBytes(nonce);
            return nonce.ToUInt64(0);
        }

        private void InitializeConsensus(byte view_number)
        {
            lock (context)
            {
                if (view_number == 0)
                    context.Reset(wallet);
                else
                    context.ChangeView(view_number);
                if (context.MyIndex < 0) return;
                Log($"initialize: height={context.BlockIndex} view={view_number} index={context.MyIndex} role={(context.MyIndex == context.PrimaryIndex ? ConsensusState.Primary : ConsensusState.Backup)}");
                if (context.MyIndex == context.PrimaryIndex)
                {
                    context.State |= ConsensusState.Primary;
                    timer_height = context.BlockIndex;
                    timer_view = view_number;
                    TimeSpan span = DateTime.Now - block_received_time;
                    if (span >= Blockchain.TimePerBlock)
                        timer.Change(0, Timeout.Infinite);
                    else
                        timer.Change(Blockchain.TimePerBlock - span, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    context.State = ConsensusState.Backup;
                    timer_height = context.BlockIndex;
                    timer_view = view_number;
                    timer.Change(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock << (view_number + 1)), Timeout.InfiniteTimeSpan);
                }
            }
        }

        private void LocalNode_InventoryReceived(object sender, IInventory inventory)
        {
            ConsensusPayload payload = inventory as ConsensusPayload;
            if (payload != null)
            {
                lock (context)
                {
                    if (payload.ValidatorIndex == context.MyIndex) return;
                    if (payload.Version != ConsensusContext.Version || payload.PrevHash != context.PrevHash || payload.BlockIndex != context.BlockIndex)
                        return;
                    if (payload.ValidatorIndex >= context.Validators.Length) return;
                    ConsensusMessage message = ConsensusMessage.DeserializeFrom(payload.Data);
                    if (message.ViewNumber != context.ViewNumber && message.Type != ConsensusMessageType.ChangeView)
                        return;
                    switch (message.Type)
                    {
                        case ConsensusMessageType.ChangeView:
                            OnChangeViewReceived(payload, (ChangeView)message);
                            break;
                        case ConsensusMessageType.PerpareRequest:
                            OnPerpareRequestReceived(payload, (PerpareRequest)message);
                            break;
                        case ConsensusMessageType.PerpareResponse:
                            OnPerpareResponseReceived(payload, (PerpareResponse)message);
                            break;
                    }
                }
            }
        }

        private void LocalNode_InventoryReceiving(object sender, InventoryReceivingEventArgs e)
        {
            Transaction tx = e.Inventory as Transaction;
            if (tx != null)
            {
                lock (context)
                {
                    if (!context.State.HasFlag(ConsensusState.Backup) || !context.State.HasFlag(ConsensusState.RequestReceived) || context.State.HasFlag(ConsensusState.SignatureSent) || context.State.HasFlag(ConsensusState.ViewChanging))
                        return;
                    if (context.Transactions.ContainsKey(tx.Hash)) return;
                    if (!context.TransactionHashes.Contains(tx.Hash)) return;
                    AddTransaction(tx, true);
                    e.Cancel = true;
                }
            }
        }

        protected virtual void Log(string message)
        {
        }

        private void OnChangeViewReceived(ConsensusPayload payload, ChangeView message)
        {
            Log($"{nameof(OnChangeViewReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex} nv={message.NewViewNumber}");
            if (message.NewViewNumber <= context.ExpectedView[payload.ValidatorIndex])
                return;
            context.ExpectedView[payload.ValidatorIndex] = message.NewViewNumber;
            CheckExpectedView(message.NewViewNumber);
        }

        private void OnPerpareRequestReceived(ConsensusPayload payload, PerpareRequest message)
        {
            Log($"{nameof(OnPerpareRequestReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex} tx={message.TransactionHashes.Length}");
            if (!context.State.HasFlag(ConsensusState.Backup) || context.State.HasFlag(ConsensusState.RequestReceived))
                return;
            if (payload.ValidatorIndex != context.PrimaryIndex) return;
            if (payload.Timestamp <= Blockchain.Default.GetHeader(context.PrevHash).Timestamp || payload.Timestamp > DateTime.Now.AddMinutes(10).ToTimestamp())
            {
                Log($"Timestamp incorrect: {payload.Timestamp}");
                return;
            }
            context.State |= ConsensusState.RequestReceived;
            context.Timestamp = payload.Timestamp;
            context.Nonce = message.Nonce;
            context.NextConsensus = message.NextConsensus;
            context.TransactionHashes = message.TransactionHashes;
            context.Transactions = new Dictionary<UInt256, Transaction>();
            if (!Crypto.Default.VerifySignature(context.MakeHeader().GetHashData(), message.Signature, context.Validators[payload.ValidatorIndex].EncodePoint(false))) return;
            context.Signatures = new byte[context.Validators.Length][];
            context.Signatures[payload.ValidatorIndex] = message.Signature;
            Dictionary<UInt256, Transaction> mempool = LocalNode.GetMemoryPool().ToDictionary(p => p.Hash);
            foreach (UInt256 hash in context.TransactionHashes.Skip(1))
                if (mempool.ContainsKey(hash))
                    if (!AddTransaction(mempool[hash], false))
                        return;
            if (!AddTransaction(message.MinerTransaction, true)) return;
            LocalNode.AllowHashes(context.TransactionHashes.Except(context.Transactions.Keys));
            if (context.Transactions.Count < context.TransactionHashes.Length)
                localNode.SynchronizeMemoryPool();
        }

        private void OnPerpareResponseReceived(ConsensusPayload payload, PerpareResponse message)
        {
            Log($"{nameof(OnPerpareResponseReceived)}: height={payload.BlockIndex} view={message.ViewNumber} index={payload.ValidatorIndex}");
            if (context.State.HasFlag(ConsensusState.BlockSent)) return;
            if (context.Signatures[payload.ValidatorIndex] != null) return;
            Block header = context.MakeHeader();
            if (header == null || !Crypto.Default.VerifySignature(header.GetHashData(), message.Signature, context.Validators[payload.ValidatorIndex].EncodePoint(false))) return;
            context.Signatures[payload.ValidatorIndex] = message.Signature;
            CheckSignatures();
        }

        private void OnTimeout(object state)
        {
            lock (context)
            {
                if (timer_height != context.BlockIndex || timer_view != context.ViewNumber) return;
                Log($"timeout: height={timer_height} view={timer_view} state={context.State}");
                if (context.State.HasFlag(ConsensusState.Primary) && !context.State.HasFlag(ConsensusState.RequestSent))
                {
                    Log($"send perpare request: height={timer_height} view={timer_view}");
                    context.State |= ConsensusState.RequestSent;
                    if (!context.State.HasFlag(ConsensusState.SignatureSent))
                    {
                        context.Timestamp = Math.Max(DateTime.Now.ToTimestamp(), Blockchain.Default.GetHeader(context.PrevHash).Timestamp + 1);
                        context.Nonce = GetNonce();
                        List<Transaction> transactions = LocalNode.GetMemoryPool().Where(p => CheckPolicy(p)).ToList();
                        if (transactions.Count >= MaxTransactionsPerBlock)
                            transactions = transactions.OrderByDescending(p => p.NetworkFee / p.Size).Take(MaxTransactionsPerBlock - 1).ToList();
                        transactions.Insert(0, CreateMinerTransaction(transactions, context.BlockIndex, context.Nonce));
                        context.TransactionHashes = transactions.Select(p => p.Hash).ToArray();
                        context.Transactions = transactions.ToDictionary(p => p.Hash);
                        context.NextConsensus = Blockchain.GetConsensusAddress(Blockchain.Default.GetValidators(transactions).ToArray());
                        context.Signatures[context.MyIndex] = context.MakeHeader().Sign(wallet.GetKey(context.Validators[context.MyIndex]));
                    }
                    SignAndRelay(context.MakePerpareRequest());
                    timer.Change(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock << (timer_view + 1)), Timeout.InfiniteTimeSpan);
                }
                else if ((context.State.HasFlag(ConsensusState.Primary) && context.State.HasFlag(ConsensusState.RequestSent)) || context.State.HasFlag(ConsensusState.Backup))
                {
                    RequestChangeView();
                }
            }
        }

        private void RequestChangeView()
        {
            context.State |= ConsensusState.ViewChanging;
            context.ExpectedView[context.MyIndex]++;
            Log($"request change view: height={context.BlockIndex} view={context.ViewNumber} nv={context.ExpectedView[context.MyIndex]} state={context.State}");
            timer.Change(TimeSpan.FromSeconds(Blockchain.SecondsPerBlock << (context.ExpectedView[context.MyIndex] + 1)), Timeout.InfiniteTimeSpan);
            SignAndRelay(context.MakeChangeView());
            CheckExpectedView(context.ExpectedView[context.MyIndex]);
        }

        private void SignAndRelay(ConsensusPayload payload)
        {
            SignatureContext sc;
            try
            {
                sc = new SignatureContext(payload);
            }
            catch (InvalidOperationException)
            {
                return;
            }
            wallet.Sign(sc);
            sc.Verifiable.Scripts = sc.GetScripts();
            localNode.RelayDirectly(payload);
        }

        public void Start()
        {
            Log("OnStart");
            started = true;
            Blockchain.PersistCompleted += Blockchain_PersistCompleted;
            LocalNode.InventoryReceiving += LocalNode_InventoryReceiving;
            LocalNode.InventoryReceived += LocalNode_InventoryReceived;
            InitializeConsensus(0);
        }
    }
}
