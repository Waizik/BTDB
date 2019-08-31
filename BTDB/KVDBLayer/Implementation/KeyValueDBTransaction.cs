using System;
using System.Collections.Generic;
using BTDB.Buffer;
using BTDB.KVDBLayer.BTree;

namespace BTDB.KVDBLayer
{
    class KeyValueDBTransaction : IKeyValueDBTransaction
    {
        readonly KeyValueDB _keyValueDB;
        IBTreeRootNode _btreeRoot;
        readonly List<NodeIdxPair> _stack = new List<NodeIdxPair>();
        byte[] _prefix;
        bool _writing;
        readonly bool _readOnly;
        bool _preapprovedWriting;
        long _prefixKeyStart;
        long _prefixKeyCount;
        long _keyIndex;
        bool _temporaryCloseTransactionLog;

        public KeyValueDBTransaction(KeyValueDB keyValueDB, IBTreeRootNode btreeRoot, bool writing, bool readOnly)
        {
            _preapprovedWriting = writing;
            _readOnly = readOnly;
            _keyValueDB = keyValueDB;
            _btreeRoot = btreeRoot;
            _prefix = Array.Empty<byte>();
            _prefixKeyStart = 0;
            _prefixKeyCount = -1;
            _keyIndex = -1;
            _keyValueDB.StartedUsingBTreeRoot(_btreeRoot);
        }

        ~KeyValueDBTransaction()
        {
            if (_btreeRoot != null)
            {
                Dispose();
                _keyValueDB.Logger?.ReportTransactionLeak(this);
            }
        }

        internal IBTreeRootNode BtreeRoot => _btreeRoot;

        public void SetKeyPrefix(ByteBuffer prefix)
        {
            _prefix = prefix.ToByteArray();
            _prefixKeyStart = _prefix.Length == 0 ? 0 : -1;
            _prefixKeyCount = -1;
            InvalidateCurrentKey();
        }

        public bool FindFirstKey()
        {
            return SetKeyIndex(0);
        }

        public bool FindLastKey()
        {
            var count = GetKeyValueCount();
            if (count <= 0) return false;
            return SetKeyIndex(count - 1);
        }

        public bool FindPreviousKey()
        {
            if (_keyIndex < 0) return FindLastKey();
            if (BtreeRoot.FindPreviousKey(_stack))
            {
                if (CheckPrefixIn(GetCurrentKeyFromStack()))
                {
                    _keyIndex--;
                    return true;
                }
            }
            InvalidateCurrentKey();
            return false;
        }

        public bool FindNextKey()
        {
            if (_keyIndex < 0) return FindFirstKey();
            if (BtreeRoot.FindNextKey(_stack))
            {
                if (CheckPrefixIn(GetCurrentKeyFromStack()))
                {
                    _keyIndex++;
                    return true;
                }
            }
            InvalidateCurrentKey();
            return false;
        }

        public FindResult Find(ByteBuffer key)
        {
            return BtreeRoot.FindKey(_stack, out _keyIndex, _prefix, key);
        }

        public bool CreateOrUpdateKeyValue(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            return CreateOrUpdateKeyValue(ByteBuffer.NewAsync(key), ByteBuffer.NewAsync(value));
        }

        public bool CreateOrUpdateKeyValue(ByteBuffer key, ByteBuffer value)
        {
            MakeWritable();
            uint valueFileId;
            uint valueOfs;
            int valueSize;
            _keyValueDB.WriteCreateOrUpdateCommand(_prefix, key, value, out valueFileId, out valueOfs, out valueSize);
            var ctx = new CreateOrUpdateCtx
            {
                KeyPrefix = _prefix,
                Key = key,
                ValueFileId = valueFileId,
                ValueOfs = valueOfs,
                ValueSize = valueSize,
                Stack = _stack
            };
            BtreeRoot.CreateOrUpdate(ctx);
            _keyIndex = ctx.KeyIndex;
            if (ctx.Created && _prefixKeyCount >= 0) _prefixKeyCount++;
            return ctx.Created;
        }

        void MakeWritable()
        {
            if (_writing) return;
            if (_preapprovedWriting)
            {
                _writing = true;
                _preapprovedWriting = false;
                _keyValueDB.WriteStartTransaction();
                return;
            }
            if (_readOnly)
            {
                throw new BTDBTransactionRetryException("Cannot write from readOnly transaction");
            }
            var oldBTreeRoot = BtreeRoot;
            _btreeRoot = _keyValueDB.MakeWritableTransaction(this, oldBTreeRoot);
            _keyValueDB.StartedUsingBTreeRoot(_btreeRoot);
            _keyValueDB.FinishedUsingBTreeRoot(oldBTreeRoot);
            _btreeRoot.DescriptionForLeaks = _descriptionForLeaks;
            _writing = true;
            InvalidateCurrentKey();
            _keyValueDB.WriteStartTransaction();
        }

        public long GetKeyValueCount()
        {
            if (_prefixKeyCount >= 0) return _prefixKeyCount;
            if (_prefix.Length == 0)
            {
                _prefixKeyCount = BtreeRoot.CalcKeyCount();
                return _prefixKeyCount;
            }
            CalcPrefixKeyStart();
            if (_prefixKeyStart < 0)
            {
                _prefixKeyCount = 0;
                return 0;
            }
            _prefixKeyCount = BtreeRoot.FindLastWithPrefix(_prefix) - _prefixKeyStart + 1;
            return _prefixKeyCount;
        }

        public long GetKeyIndex()
        {
            if (_keyIndex < 0) return -1;
            CalcPrefixKeyStart();
            return _keyIndex - _prefixKeyStart;
        }

        void CalcPrefixKeyStart()
        {
            if (_prefixKeyStart >= 0) return;
            if (BtreeRoot.FindKey(new List<NodeIdxPair>(), out _prefixKeyStart, _prefix, ByteBuffer.NewEmpty()) == FindResult.NotFound)
            {
                _prefixKeyStart = -1;
            }
        }

        public bool SetKeyIndex(long index)
        {
            CalcPrefixKeyStart();
            if (_prefixKeyStart < 0)
            {
                InvalidateCurrentKey();
                return false;
            }
            _keyIndex = index + _prefixKeyStart;
            if (_keyIndex >= BtreeRoot.CalcKeyCount())
            {
                InvalidateCurrentKey();
                return false;
            }
            BtreeRoot.FillStackByIndex(_stack, _keyIndex);
            if (_prefixKeyCount >= 0)
                return true;
            var key = GetCurrentKeyFromStack();
            if (CheckPrefixIn(key))
            {
                return true;
            }
            InvalidateCurrentKey();
            return false;
        }

        bool CheckPrefixIn(ByteBuffer key)
        {
            return BTreeRoot.KeyStartsWithPrefix(_prefix, key);
        }

        ByteBuffer GetCurrentKeyFromStack()
        {
            var nodeIdxPair = _stack[_stack.Count - 1];
            return ((IBTreeLeafNode)nodeIdxPair.Node).GetKey(nodeIdxPair.Idx);
        }

        public void InvalidateCurrentKey()
        {
            _keyIndex = -1;
            _stack.Clear();
        }

        public bool IsValidKey()
        {
            return _keyIndex >= 0;
        }

        public ByteBuffer GetKey()
        {
            if (!IsValidKey()) return ByteBuffer.NewEmpty();
            var wholeKey = GetCurrentKeyFromStack();
            return ByteBuffer.NewAsync(wholeKey.Buffer, wholeKey.Offset + _prefix.Length, wholeKey.Length - _prefix.Length);
        }

        public ByteBuffer GetValue()
        {
            if (!IsValidKey()) return ByteBuffer.NewEmpty();
            var nodeIdxPair = _stack[_stack.Count - 1];
            var leafMember = ((IBTreeLeafNode)nodeIdxPair.Node).GetMemberValue(nodeIdxPair.Idx);
            try
            {
                return _keyValueDB.ReadValue(leafMember.ValueFileId, leafMember.ValueOfs, leafMember.ValueSize);
            }
            catch (BTDBException ex)
            {
                var oldestRoot = (IBTreeRootNode)_keyValueDB.ReferenceAndGetOldestRoot();
                var lastCommited = (IBTreeRootNode)_keyValueDB.ReferenceAndGetLastCommitted();
                // no need to dereference roots because we know it is managed
                throw new BTDBException($"GetValue failed in TrId:{BtreeRoot.TransactionId},TRL:{BtreeRoot.TrLogFileId},Ofs:{BtreeRoot.TrLogOffset},ComUlong:{BtreeRoot.CommitUlong} and LastTrId:{lastCommited.TransactionId},ComUlong:{lastCommited.CommitUlong} OldestTrId:{oldestRoot.TransactionId},TRL:{oldestRoot.TrLogFileId},ComUlong:{oldestRoot.CommitUlong} innerMessage:{ex.Message}", ex);
            }
        }

        public ReadOnlySpan<byte> GetValueAsReadOnlySpan()
        {
            return GetValue().AsSyncReadOnlySpan();
        }

        void EnsureValidKey()
        {
            if (_keyIndex < 0)
            {
                throw new InvalidOperationException("Current key is not valid");
            }
        }

        public void SetValue(ByteBuffer value)
        {
            EnsureValidKey();
            var keyIndexBackup = _keyIndex;
            MakeWritable();
            if (_keyIndex != keyIndexBackup)
            {
                _keyIndex = keyIndexBackup;
                BtreeRoot.FillStackByIndex(_stack, _keyIndex);
            }
            var nodeIdxPair = _stack[_stack.Count - 1];
            var memberValue = ((IBTreeLeafNode)nodeIdxPair.Node).GetMemberValue(nodeIdxPair.Idx);
            var memberKey = ((IBTreeLeafNode)nodeIdxPair.Node).GetKey(nodeIdxPair.Idx);
            _keyValueDB.WriteCreateOrUpdateCommand(Array.Empty<byte>(), memberKey, value, out memberValue.ValueFileId, out memberValue.ValueOfs, out memberValue.ValueSize);
            ((IBTreeLeafNode)nodeIdxPair.Node).SetMemberValue(nodeIdxPair.Idx, memberValue);
        }

        public void EraseCurrent()
        {
            EnsureValidKey();
            var keyIndex = _keyIndex;
            MakeWritable();
            InvalidateCurrentKey();
            _prefixKeyCount--;
            BtreeRoot.FillStackByIndex(_stack, keyIndex);
            _keyValueDB.WriteEraseOneCommand(GetCurrentKeyFromStack());
            BtreeRoot.EraseRange(keyIndex, keyIndex);
        }

        public void EraseAll()
        {
            EraseRange(0, long.MaxValue);
        }

        public void EraseRange(long firstKeyIndex, long lastKeyIndex)
        {
            if (firstKeyIndex < 0) firstKeyIndex = 0;
            if (lastKeyIndex >= GetKeyValueCount()) lastKeyIndex = _prefixKeyCount - 1;
            if (lastKeyIndex < firstKeyIndex) return;
            MakeWritable();
            firstKeyIndex += _prefixKeyStart;
            lastKeyIndex += _prefixKeyStart;
            InvalidateCurrentKey();
            _prefixKeyCount -= lastKeyIndex - firstKeyIndex + 1;
            BtreeRoot.FillStackByIndex(_stack, firstKeyIndex);
            if (firstKeyIndex == lastKeyIndex)
            {
                _keyValueDB.WriteEraseOneCommand(GetCurrentKeyFromStack());
            }
            else
            {
                var firstKey = GetCurrentKeyFromStack();
                BtreeRoot.FillStackByIndex(_stack, lastKeyIndex);
                _keyValueDB.WriteEraseRangeCommand(firstKey, GetCurrentKeyFromStack());
            }
            BtreeRoot.EraseRange(firstKeyIndex, lastKeyIndex);
        }

        public bool IsWriting()
        {
            return _writing;
        }

        public ulong GetCommitUlong()
        {
            return BtreeRoot.CommitUlong;
        }

        public void SetCommitUlong(ulong value)
        {
            if (BtreeRoot.CommitUlong != value)
            {
                MakeWritable();
                BtreeRoot.CommitUlong = value;
            }
        }

        public void NextCommitTemporaryCloseTransactionLog()
        {
            MakeWritable();
            _temporaryCloseTransactionLog = true;
        }

        public void Commit()
        {
            if (BtreeRoot == null) throw new BTDBException("Transaction already commited or disposed");
            InvalidateCurrentKey();
            var currentBtreeRoot = _btreeRoot;
            _keyValueDB.FinishedUsingBTreeRoot(_btreeRoot);
            _btreeRoot = null;
            GC.SuppressFinalize(this);
            if (_preapprovedWriting)
            {
                _preapprovedWriting = false;
                _keyValueDB.RevertWritingTransaction(true);
            }
            else if (_writing)
            {
                _keyValueDB.CommitWritingTransaction(currentBtreeRoot, _temporaryCloseTransactionLog);
                _writing = false;
            }
        }

        public void Dispose()
        {
            if (_writing || _preapprovedWriting)
            {
                _keyValueDB.RevertWritingTransaction(_preapprovedWriting);
                _writing = false;
                _preapprovedWriting = false;
            }
            if (_btreeRoot == null) return;
            _keyValueDB.FinishedUsingBTreeRoot(_btreeRoot);
            _btreeRoot = null;
            GC.SuppressFinalize(this);
        }

        public long GetTransactionNumber()
        {
            return _btreeRoot.TransactionId;
        }

        public KeyValuePair<uint, uint> GetStorageSizeOfCurrentKey()
        {
            if (!IsValidKey()) return new KeyValuePair<uint, uint>();
            var nodeIdxPair = _stack[_stack.Count - 1];
            var leafMember = ((IBTreeLeafNode)nodeIdxPair.Node).GetMemberValue(nodeIdxPair.Idx);

            return new KeyValuePair<uint, uint>(
                (uint)((IBTreeLeafNode)nodeIdxPair.Node).GetKey(nodeIdxPair.Idx).Length,
                _keyValueDB.CalcValueSize(leafMember.ValueFileId, leafMember.ValueOfs, leafMember.ValueSize));
        }

        public byte[] GetKeyPrefix()
        {
            return _prefix;
        }

        public ulong GetUlong(uint idx)
        {
            return BtreeRoot.GetUlong(idx);
        }

        public void SetUlong(uint idx, ulong value)
        {
            if (BtreeRoot.GetUlong(idx) != value)
            {
                MakeWritable();
                BtreeRoot.SetUlong(idx, value);
            }
        }

        public uint GetUlongCount()
        {
            return BtreeRoot.UlongsArray == null ? 0U : (uint)BtreeRoot.UlongsArray.Length;
        }

        string _descriptionForLeaks;
        public string DescriptionForLeaks
        {
            get { return _descriptionForLeaks; }
            set
            {
                _descriptionForLeaks = value;
                if (_preapprovedWriting || _writing) _btreeRoot.DescriptionForLeaks = value;
            }
        }

        public bool RollbackAdvised { get; set; }
    }
}
