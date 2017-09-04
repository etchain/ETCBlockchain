﻿using AntShares.Core;
using AntShares.Cryptography;
using AntShares.IO.Caching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace AntShares.Wallets
{
    public abstract class Wallet : IDisposable
    {
        public event EventHandler BalanceChanged;

        public static readonly byte AddressVersion = Settings.Default.AddressVersion;

        private readonly string path;
        private readonly byte[] iv;
        private readonly byte[] masterKey;
        private readonly Dictionary<UInt160, KeyPair> keys;
        private readonly Dictionary<UInt160, Contract> contracts;
        private readonly HashSet<UInt160> watchOnly;
        private readonly TrackableCollection<CoinReference, Coin> coins;
        private uint current_height;

        private readonly Thread thread;
        private bool isrunning = true;

        protected string DbPath => path;
        protected object SyncRoot { get; } = new object();
        public uint WalletHeight => current_height;
        protected abstract Version Version { get; }

        private Wallet(string path, byte[] passwordKey, bool create)
        {
            this.path = path;
            if (create)
            {
                this.iv = new byte[16];
                this.masterKey = new byte[32];
                this.keys = new Dictionary<UInt160, KeyPair>();
                this.contracts = new Dictionary<UInt160, Contract>();
                this.watchOnly = new HashSet<UInt160>();
                this.coins = new TrackableCollection<CoinReference, Coin>();
                this.current_height = Blockchain.Default?.HeaderHeight + 1 ?? 0;
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(iv);
                    rng.GetBytes(masterKey);
                }
                BuildDatabase();
                SaveStoredData("PasswordHash", passwordKey.Sha256());
                SaveStoredData("IV", iv);
                SaveStoredData("MasterKey", masterKey.AesEncrypt(passwordKey, iv));
                SaveStoredData("Version", new[] { Version.Major, Version.Minor, Version.Build, Version.Revision }.Select(p => BitConverter.GetBytes(p)).SelectMany(p => p).ToArray());
                SaveStoredData("Height", BitConverter.GetBytes(current_height));
#if NET461
                ProtectedMemory.Protect(masterKey, MemoryProtectionScope.SameProcess);
#endif
            }
            else
            {
                byte[] passwordHash = LoadStoredData("PasswordHash");
                if (passwordHash != null && !passwordHash.SequenceEqual(passwordKey.Sha256()))
                    throw new CryptographicException();
                this.iv = LoadStoredData("IV");
                this.masterKey = LoadStoredData("MasterKey").AesDecrypt(passwordKey, iv);
#if NET461
                ProtectedMemory.Protect(masterKey, MemoryProtectionScope.SameProcess);
#endif
                this.keys = LoadKeyPairs().ToDictionary(p => p.PublicKeyHash);
                this.contracts = LoadContracts().ToDictionary(p => p.ScriptHash);
                this.watchOnly = new HashSet<UInt160>(LoadWatchOnly());
                this.coins = new TrackableCollection<CoinReference, Coin>(LoadCoins());
                this.current_height = LoadStoredData("Height").ToUInt32(0);
            }
            Array.Clear(passwordKey, 0, passwordKey.Length);
            this.thread = new Thread(ProcessBlocks);
            this.thread.IsBackground = true;
            this.thread.Name = "Wallet.ProcessBlocks";
            this.thread.Start();
        }

        protected Wallet(string path, string password, bool create)
            : this(path, password.ToAesKey(), create)
        {
        }

        protected Wallet(string path, SecureString password, bool create)
            : this(path, password.ToAesKey(), create)
        {
        }

        public virtual void AddContract(Contract contract)
        {
            lock (keys)
            {
                if (!keys.ContainsKey(contract.PublicKeyHash))
                    throw new InvalidOperationException();
                lock (contracts)
                    lock (watchOnly)
                    {
                        contracts[contract.ScriptHash] = contract;
                        watchOnly.Remove(contract.ScriptHash);
                    }
            }
        }

        public virtual void AddWatchOnly(UInt160 scriptHash)
        {
            lock (contracts)
            {
                if (contracts.ContainsKey(scriptHash))
                    return;
                lock (watchOnly)
                {
                    watchOnly.Add(scriptHash);
                }
            }
        }

        protected virtual void BuildDatabase()
        {
        }

        public bool ChangePassword(string password_old, string password_new)
        {
            if (!VerifyPassword(password_old)) return false;
            byte[] passwordKey = password_new.ToAesKey();
#if NET461
            using (new ProtectedMemoryContext(masterKey, MemoryProtectionScope.SameProcess))
#endif
            {
                try
                {
                    SaveStoredData("PasswordHash", passwordKey.Sha256());
                    SaveStoredData("MasterKey", masterKey.AesEncrypt(passwordKey, iv));
                    return true;
                }
                finally
                {
                    Array.Clear(passwordKey, 0, passwordKey.Length);
                }
            }
        }

        private AddressState CheckAddressState(UInt160 scriptHash)
        {
            lock (contracts)
            {
                if (contracts.ContainsKey(scriptHash))
                    return AddressState.InWallet;
            }
            lock (watchOnly)
            {
                if (watchOnly.Contains(scriptHash))
                    return AddressState.InWallet | AddressState.WatchOnly;
            }
            return AddressState.None;
        }

        public bool ContainsKey(Cryptography.ECC.ECPoint publicKey)
        {
            return ContainsKey(publicKey.EncodePoint(true).ToScriptHash());
        }

        public bool ContainsKey(UInt160 publicKeyHash)
        {
            lock (keys)
            {
                return keys.ContainsKey(publicKeyHash);
            }
        }

        public bool ContainsAddress(UInt160 scriptHash)
        {
            return CheckAddressState(scriptHash).HasFlag(AddressState.InWallet);
        }

        public KeyPair CreateKey()
        {
            byte[] privateKey = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(privateKey);
            }
            KeyPair key = CreateKey(privateKey);
            Array.Clear(privateKey, 0, privateKey.Length);
            return key;
        }

        public virtual KeyPair CreateKey(byte[] privateKey)
        {
            KeyPair key = new KeyPair(privateKey);
            lock (keys)
            {
                keys[key.PublicKeyHash] = key;
            }
            return key;
        }

        protected byte[] DecryptPrivateKey(byte[] encryptedPrivateKey)
        {
            if (encryptedPrivateKey == null) throw new ArgumentNullException(nameof(encryptedPrivateKey));
            if (encryptedPrivateKey.Length != 96) throw new ArgumentException();
#if NET461
            using (new ProtectedMemoryContext(masterKey, MemoryProtectionScope.SameProcess))
#endif
            {
                return encryptedPrivateKey.AesDecrypt(masterKey, iv);
            }
        }

        public virtual bool DeleteKey(UInt160 publicKeyHash)
        {
            lock (keys)
            {
                lock (contracts)
                {
                    foreach (Contract contract in contracts.Values.Where(p => p.PublicKeyHash == publicKeyHash).ToArray())
                    {
                        DeleteAddress(contract.ScriptHash);
                    }
                }
                return keys.Remove(publicKeyHash);
            }
        }

        public virtual bool DeleteAddress(UInt160 scriptHash)
        {
            lock (contracts)
                lock (watchOnly)
                    lock (coins)
                    {
                        foreach (CoinReference key in coins.Where(p => p.Output.ScriptHash == scriptHash).Select(p => p.Reference).ToArray())
                        {
                            coins.Remove(key);
                        }
                        coins.Commit();
                        return contracts.Remove(scriptHash) || watchOnly.Remove(scriptHash);
                    }
        }

        public virtual void Dispose()
        {
            isrunning = false;
            if (!thread.ThreadState.HasFlag(ThreadState.Unstarted)) thread.Join();
        }

        protected byte[] EncryptPrivateKey(byte[] decryptedPrivateKey)
        {
#if NET461
            using (new ProtectedMemoryContext(masterKey, MemoryProtectionScope.SameProcess))
#endif
            {
                return decryptedPrivateKey.AesEncrypt(masterKey, iv);
            }
        }

        public IEnumerable<Coin> FindUnspentCoins()
        {
            return GetCoins().Where(p => p.State.HasFlag(CoinState.Confirmed) && !p.State.HasFlag(CoinState.Spent) && !p.State.HasFlag(CoinState.Locked) && !p.State.HasFlag(CoinState.Frozen) && !p.State.HasFlag(CoinState.WatchOnly));
        }

        public virtual Coin[] FindUnspentCoins(UInt256 asset_id, Fixed8 amount)
        {
            return FindUnspentCoins(FindUnspentCoins(), asset_id, amount);
        }

        protected static Coin[] FindUnspentCoins(IEnumerable<Coin> unspents, UInt256 asset_id, Fixed8 amount)
        {
            Coin[] unspents_asset = unspents.Where(p => p.Output.AssetId == asset_id).ToArray();
            Fixed8 sum = unspents_asset.Sum(p => p.Output.Value);
            if (sum < amount) return null;
            if (sum == amount) return unspents_asset;
            Coin[] unspents_ordered = unspents_asset.OrderByDescending(p => p.Output.Value).ToArray();
            int i = 0;
            while (unspents_ordered[i].Output.Value <= amount)
                amount -= unspents_ordered[i++].Output.Value;
            if (amount == Fixed8.Zero)
                return unspents_ordered.Take(i).ToArray();
            else
                return unspents_ordered.Take(i).Concat(new[] { unspents_ordered.Last(p => p.Output.Value >= amount) }).ToArray();
        }

        public KeyPair GetKey(Cryptography.ECC.ECPoint publicKey)
        {
            return GetKey(publicKey.EncodePoint(true).ToScriptHash());
        }

        public KeyPair GetKey(UInt160 publicKeyHash)
        {
            lock (keys)
            {
                if (!keys.ContainsKey(publicKeyHash)) return null;
                return keys[publicKeyHash];
            }
        }

        public KeyPair GetKeyByScriptHash(UInt160 scriptHash)
        {
            lock (keys)
                lock (contracts)
                {
                    if (!contracts.ContainsKey(scriptHash)) return null;
                    return keys[contracts[scriptHash].PublicKeyHash];
                }
        }

        public IEnumerable<KeyPair> GetKeys()
        {
            lock (keys)
            {
                foreach (var pair in keys)
                {
                    yield return pair.Value;
                }
            }
        }

        public IEnumerable<UInt160> GetAddresses()
        {
            lock (contracts)
            {
                foreach (var pair in contracts)
                    yield return pair.Key;
            }
            lock (watchOnly)
            {
                foreach (UInt160 hash in watchOnly)
                    yield return hash;
            }
        }

        public Fixed8 GetAvailable(UInt256 asset_id)
        {
            return FindUnspentCoins().Where(p => p.Output.AssetId.Equals(asset_id)).Sum(p => p.Output.Value);
        }

        public Fixed8 GetBalance(UInt256 asset_id)
        {
            return GetCoins().Where(p => !p.State.HasFlag(CoinState.Spent) && p.Output.AssetId.Equals(asset_id)).Sum(p => p.Output.Value);
        }

        public virtual UInt160 GetChangeAddress()
        {
            lock (contracts)
            {
                return contracts.Values.FirstOrDefault(p => p.IsStandard)?.ScriptHash ?? contracts.Keys.FirstOrDefault();
            }
        }

        public IEnumerable<Coin> GetCoins()
        {
            lock (coins)
            {
                foreach (Coin coin in coins)
                    yield return coin;
            }
        }

        public Contract GetContract(UInt160 scriptHash)
        {
            lock (contracts)
            {
                if (!contracts.ContainsKey(scriptHash)) return null;
                return contracts[scriptHash];
            }
        }

        public IEnumerable<Contract> GetContracts()
        {
            lock (contracts)
            {
                foreach (var pair in contracts)
                {
                    yield return pair.Value;
                }
            }
        }

        public IEnumerable<Contract> GetContracts(UInt160 publicKeyHash)
        {
            lock (contracts)
            {
                foreach (Contract contract in contracts.Values.Where(p => p.PublicKeyHash.Equals(publicKeyHash)))
                {
                    yield return contract;
                }
            }
        }

        public static byte[] GetPrivateKeyFromWIF(string wif)
        {
            if (wif == null) throw new ArgumentNullException();
            byte[] data = Base58.Decode(wif);
            if (data.Length != 38 || data[0] != 0x80 || data[33] != 0x01)
                throw new FormatException();
            byte[] checksum = Crypto.Default.Hash256(data.Take(data.Length - 4).ToArray());
            if (!data.Skip(data.Length - 4).SequenceEqual(checksum.Take(4)))
                throw new FormatException();
            byte[] privateKey = new byte[32];
            Buffer.BlockCopy(data, 1, privateKey, 0, privateKey.Length);
            Array.Clear(data, 0, data.Length);
            return privateKey;
        }

        public abstract IEnumerable<T> GetTransactions<T>() where T : Transaction;

        public IEnumerable<Coin> GetUnclaimedCoins()
        {
            lock (coins)
            {
                foreach (var coin in coins)
                {
                    if (!coin.Output.AssetId.Equals(Blockchain.SystemShare.Hash)) continue;
                    if (!coin.State.HasFlag(CoinState.Confirmed)) continue;
                    if (!coin.State.HasFlag(CoinState.Spent)) continue;
                    if (coin.State.HasFlag(CoinState.Claimed)) continue;
                    if (coin.State.HasFlag(CoinState.Frozen)) continue;
                    if (coin.State.HasFlag(CoinState.WatchOnly)) continue;
                    yield return coin;
                }
            }
        }

        public KeyPair Import(X509Certificate2 cert)
        {
            byte[] privateKey;
            using (ECDsa ecdsa = cert.GetECDsaPrivateKey())
            {
#if NET461
                privateKey = ((ECDsaCng)ecdsa).Key.Export(CngKeyBlobFormat.EccPrivateBlob);
#else
                privateKey = ecdsa.ExportParameters(true).D;
#endif
            }
            KeyPair key = CreateKey(privateKey);
            Array.Clear(privateKey, 0, privateKey.Length);
            return key;
        }

        public KeyPair Import(string wif)
        {
            byte[] privateKey = GetPrivateKeyFromWIF(wif);
            KeyPair key = CreateKey(privateKey);
            Array.Clear(privateKey, 0, privateKey.Length);
            return key;
        }

        protected bool IsWalletTransaction(Transaction tx)
        {
            lock (contracts)
            {
                if (tx.Outputs.Any(p => contracts.ContainsKey(p.ScriptHash)))
                    return true;
                if (tx.Scripts.Any(p => p.VerificationScript != null && contracts.ContainsKey(p.VerificationScript.ToScriptHash())))
                    return true;
            }
            lock (watchOnly)
            {
                if (tx.Outputs.Any(p => watchOnly.Contains(p.ScriptHash)))
                    return true;
                if (tx.Scripts.Any(p => p.VerificationScript != null && watchOnly.Contains(p.VerificationScript.ToScriptHash())))
                    return true;
            }
            return false;
        }

        protected abstract IEnumerable<KeyPair> LoadKeyPairs();

        protected abstract IEnumerable<Coin> LoadCoins();

        protected abstract IEnumerable<Contract> LoadContracts();

        protected abstract byte[] LoadStoredData(string name);

        protected virtual IEnumerable<UInt160> LoadWatchOnly()
        {
            return Enumerable.Empty<UInt160>();
        }

        public T MakeTransaction<T>(T tx, UInt160 change_address = null, Fixed8 fee = default(Fixed8)) where T : Transaction
        {
            if (tx.Outputs == null) tx.Outputs = new TransactionOutput[0];
            if (tx.Attributes == null) tx.Attributes = new TransactionAttribute[0];
            fee += tx.SystemFee;
            var pay_total = (typeof(T) == typeof(IssueTransaction) ? new TransactionOutput[0] : tx.Outputs).GroupBy(p => p.AssetId, (k, g) => new
            {
                AssetId = k,
                Value = g.Sum(p => p.Value)
            }).ToDictionary(p => p.AssetId);
            if (fee > Fixed8.Zero)
            {
                if (pay_total.ContainsKey(Blockchain.SystemCoin.Hash))
                {
                    pay_total[Blockchain.SystemCoin.Hash] = new
                    {
                        AssetId = Blockchain.SystemCoin.Hash,
                        Value = pay_total[Blockchain.SystemCoin.Hash].Value + fee
                    };
                }
                else
                {
                    pay_total.Add(Blockchain.SystemCoin.Hash, new
                    {
                        AssetId = Blockchain.SystemCoin.Hash,
                        Value = fee
                    });
                }
            }
            var pay_coins = pay_total.Select(p => new
            {
                AssetId = p.Key,
                Unspents = FindUnspentCoins(p.Key, p.Value.Value)
            }).ToDictionary(p => p.AssetId);
            if (pay_coins.Any(p => p.Value.Unspents == null)) return null;
            var input_sum = pay_coins.Values.ToDictionary(p => p.AssetId, p => new
            {
                AssetId = p.AssetId,
                Value = p.Unspents.Sum(q => q.Output.Value)
            });
            if (change_address == null) change_address = GetChangeAddress();
            List<TransactionOutput> outputs_new = new List<TransactionOutput>(tx.Outputs);
            foreach (UInt256 asset_id in input_sum.Keys)
            {
                if (input_sum[asset_id].Value > pay_total[asset_id].Value)
                {
                    outputs_new.Add(new TransactionOutput
                    {
                        AssetId = asset_id,
                        Value = input_sum[asset_id].Value - pay_total[asset_id].Value,
                        ScriptHash = change_address
                    });
                }
            }
            tx.Inputs = pay_coins.Values.SelectMany(p => p.Unspents).Select(p => p.Reference).ToArray();
            tx.Outputs = outputs_new.ToArray();
            return tx;
        }

        protected abstract void OnProcessNewBlock(Block block, IEnumerable<Coin> added, IEnumerable<Coin> changed, IEnumerable<Coin> deleted);
        protected abstract void OnSaveTransaction(Transaction tx, IEnumerable<Coin> added, IEnumerable<Coin> changed);

        private void ProcessBlocks()
        {
            while (isrunning)
            {
                while (current_height <= Blockchain.Default?.Height && isrunning)
                {
                    lock (SyncRoot)
                    {
                        Block block = Blockchain.Default.GetBlock(current_height);
                        if (block != null) ProcessNewBlock(block);
                    }
                }
                for (int i = 0; i < 20 && isrunning; i++)
                {
                    Thread.Sleep(100);
                }
            }
        }

        private void ProcessNewBlock(Block block)
        {
            Coin[] changeset;
            lock (contracts)
                lock (coins)
                {
                    foreach (Transaction tx in block.Transactions)
                    {
                        for (ushort index = 0; index < tx.Outputs.Length; index++)
                        {
                            TransactionOutput output = tx.Outputs[index];
                            AddressState state = CheckAddressState(output.ScriptHash);
                            if (state.HasFlag(AddressState.InWallet))
                            {
                                CoinReference key = new CoinReference
                                {
                                    PrevHash = tx.Hash,
                                    PrevIndex = index
                                };
                                if (coins.Contains(key))
                                    coins[key].State |= CoinState.Confirmed;
                                else
                                    coins.Add(new Coin
                                    {
                                        Reference = key,
                                        Output = output,
                                        State = CoinState.Confirmed
                                    });
                                if (state.HasFlag(AddressState.WatchOnly))
                                    coins[key].State |= CoinState.WatchOnly;
                            }
                        }
                    }
                    foreach (Transaction tx in block.Transactions)
                    {
                        foreach (CoinReference input in tx.Inputs)
                        {
                            if (coins.Contains(input))
                            {
                                if (coins[input].Output.AssetId.Equals(Blockchain.SystemShare.Hash))
                                    coins[input].State |= CoinState.Spent | CoinState.Confirmed;
                                else
                                    coins.Remove(input);
                            }
                        }
                    }
                    foreach (ClaimTransaction tx in block.Transactions.OfType<ClaimTransaction>())
                    {
                        foreach (CoinReference claim in tx.Claims)
                        {
                            if (coins.Contains(claim))
                            {
                                coins.Remove(claim);
                            }
                        }
                    }
                    current_height++;
                    changeset = coins.GetChangeSet();
                    OnProcessNewBlock(block, changeset.Where(p => ((ITrackable<CoinReference>)p).TrackState == TrackState.Added), changeset.Where(p => ((ITrackable<CoinReference>)p).TrackState == TrackState.Changed), changeset.Where(p => ((ITrackable<CoinReference>)p).TrackState == TrackState.Deleted));
                    coins.Commit();
                }
            if (changeset.Length > 0)
                BalanceChanged?.Invoke(this, EventArgs.Empty);
        }

        public virtual void Rebuild()
        {
            lock (SyncRoot)
                lock (coins)
                {
                    coins.Clear();
                    coins.Commit();
                    current_height = 0;
                }
        }

        protected abstract void SaveStoredData(string name, byte[] value);

        public bool SaveTransaction(Transaction tx)
        {
            Coin[] changeset;
            lock (contracts)
                lock (coins)
                {
                    if (tx.Inputs.Any(p => !coins.Contains(p) || coins[p].State.HasFlag(CoinState.Spent) || !coins[p].State.HasFlag(CoinState.Confirmed)))
                        return false;
                    foreach (CoinReference input in tx.Inputs)
                    {
                        coins[input].State |= CoinState.Spent;
                        coins[input].State &= ~CoinState.Confirmed;
                    }
                    for (ushort i = 0; i < tx.Outputs.Length; i++)
                    {
                        AddressState state = CheckAddressState(tx.Outputs[i].ScriptHash);
                        if (state.HasFlag(AddressState.InWallet))
                        {
                            Coin coin = new Coin
                            {
                                Reference = new CoinReference
                                {
                                    PrevHash = tx.Hash,
                                    PrevIndex = i
                                },
                                Output = tx.Outputs[i],
                                State = CoinState.Unconfirmed
                            };
                            if (state.HasFlag(AddressState.WatchOnly))
                                coin.State |= CoinState.WatchOnly;
                            coins.Add(coin);
                        }
                    }
                    if (tx is ClaimTransaction)
                    {
                        foreach (CoinReference claim in ((ClaimTransaction)tx).Claims)
                        {
                            coins[claim].State |= CoinState.Claimed;
                            coins[claim].State &= ~CoinState.Confirmed;
                        }
                    }
                    changeset = coins.GetChangeSet();
                    OnSaveTransaction(tx, changeset.Where(p => ((ITrackable<CoinReference>)p).TrackState == TrackState.Added), changeset.Where(p => ((ITrackable<CoinReference>)p).TrackState == TrackState.Changed));
                    coins.Commit();
                }
            if (changeset.Length > 0)
                BalanceChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public bool Sign(SignatureContext context)
        {
            bool fSuccess = false;
            foreach (UInt160 scriptHash in context.ScriptHashes)
            {
                Contract contract = GetContract(scriptHash);
                if (contract == null) continue;
                KeyPair key = GetKeyByScriptHash(scriptHash);
                if (key == null) continue;
                byte[] signature = context.Verifiable.Sign(key);
                fSuccess |= context.AddSignature(contract, key.PublicKey, signature);
            }
            return fSuccess;
        }

        public static string ToAddress(UInt160 scriptHash)
        {
            byte[] data = new byte[] { AddressVersion }.Concat(scriptHash.ToArray()).ToArray();
            return Base58.Encode(data.Concat(Crypto.Default.Hash256(data).Take(4)).ToArray());
        }

        public static UInt160 ToScriptHash(string address)
        {
            byte[] data = Base58.Decode(address);
            if (data.Length != 25)
                throw new FormatException();
            if (data[0] != AddressVersion)
                throw new FormatException();
            if (!Crypto.Default.Hash256(data.Take(21).ToArray()).Take(4).SequenceEqual(data.Skip(21)))
                throw new FormatException();
            return new UInt160(data.Skip(1).Take(20).ToArray());
        }

        public bool VerifyPassword(string password)
        {
            return password.ToAesKey().Sha256().SequenceEqual(LoadStoredData("PasswordHash"));
        }

        public bool VerifyPassword(SecureString password)
        {
            return password.ToAesKey().Sha256().SequenceEqual(LoadStoredData("PasswordHash"));
        }
    }
}
