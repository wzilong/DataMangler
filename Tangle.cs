﻿/*
The contents of this file are subject to the Mozilla Public License
Version 1.1 (the "License"); you may not use this file except in
compliance with the License. You may obtain a copy of the License at
http://www.mozilla.org/MPL/

Software distributed under the License is distributed on an "AS IS"
basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See the
License for the specific language governing rights and limitations
under the License.

The Original Code is DataMangler Key-Value Store.

The Initial Developer of the Original Code is Mozilla Corporation.

Original Author: Kevin Gadd (kevin.gadd@gmail.com)
*/

using System;
using System.Collections.Generic;
using System.Text;
using Squared.Task;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Squared.Data.Mangler.Internal;
using System.Xml.Serialization;
using System.IO.MemoryMappedFiles;
using Squared.Util;
using System.Collections.Concurrent;

namespace Squared.Data.Mangler.Internal {
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal unsafe struct IndexEntry {
        public static readonly uint Size;

        static IndexEntry () {
            Size = (uint)Marshal.SizeOf(typeof(IndexEntry));
        }

        public uint KeyOffset, DataOffset;
        public ushort KeyLength;
        public uint DataLength;
        public byte KeyType, IsValid;
    }
}

namespace Squared.Data.Mangler {
    /// <summary>
    /// Handles converting a single value from the Tangle into raw binary for storage.
    /// </summary>
    public delegate void TangleSerializer<T> (ref T input, Stream output);
    /// <summary>
    /// Handles converting a single stored value from raw binary back into its native format, when the Tangle is loading it from storage.
    /// </summary>
    public delegate void TangleDeserializer<T> (Stream input, out T output);

    public struct TangleKey {
        private static readonly Dictionary<byte, Type> TypeIdToType = new Dictionary<byte, Type>();
        private static readonly Dictionary<Type, byte> TypeToTypeId = new Dictionary<Type, byte>();

        static TangleKey () {
            RegisterType<string>();
            RegisterType<byte[]>();
            RegisterType<uint>();
            RegisterType<int>();
            RegisterType<ulong>();
            RegisterType<long>();
        }

        private static void RegisterType<T> () {
            if (TypeToTypeId.Count > 255)
                throw new InvalidOperationException("Too many registered types");

            var type = typeof(T);
            byte id = (byte)TypeToTypeId.Count;
            TypeToTypeId[type] = id;
            TypeIdToType[id] = type;
        }

        public readonly byte OriginalTypeId;
        public readonly ArraySegment<byte> KeyData;

        public TangleKey (uint key)
            : this(BitConverter.GetBytes(key), TypeToTypeId[typeof(uint)]) {
        }

        public TangleKey (ulong key)
            : this(BitConverter.GetBytes(key), TypeToTypeId[typeof(ulong)]) {
        }

        public TangleKey (int key)
            : this(BitConverter.GetBytes(key), TypeToTypeId[typeof(int)]) {
        }

        public TangleKey (long key)
            : this(BitConverter.GetBytes(key), TypeToTypeId[typeof(long)]) {
        }

        public TangleKey (string key)
            : this (Encoding.ASCII.GetBytes(key), TypeToTypeId[typeof(string)]) {
        }

        public TangleKey (byte[] array)
            : this(array, 0, array.Length, TypeToTypeId[typeof(byte[])]) {
        }

        public TangleKey (byte[] array, int offset, int count)
            : this(array, offset, count, TypeToTypeId[typeof(string)]) {
        }

        public TangleKey (byte[] array, byte originalType)
            : this(array, 0, array.Length, originalType) {
        }

        public TangleKey (byte[] array, int offset, int count, byte originalType) {
            if (count >= ushort.MaxValue)
                throw new InvalidOperationException("Key length limit exceeded");

            KeyData = new ArraySegment<byte>(array, offset, count);
            OriginalTypeId = originalType;
        }

        public Type OriginalType {
            get {
                return TypeIdToType[OriginalTypeId];
            }
        }

        public object Value {
            get {
                var type = OriginalType;

                if (type == typeof(string)) {
                    return Encoding.ASCII.GetString(KeyData.Array, KeyData.Offset, KeyData.Count);
                } else if (type == typeof(int)) {
                    return BitConverter.ToInt32(KeyData.Array, KeyData.Offset);
                } else if (type == typeof(uint)) {
                    return BitConverter.ToUInt32(KeyData.Array, KeyData.Offset);
                } else if (type == typeof(long)) {
                    return BitConverter.ToInt64(KeyData.Array, KeyData.Offset);
                } else if (type == typeof(ulong)) {
                    return BitConverter.ToUInt64(KeyData.Array, KeyData.Offset);
                } else /* if (type == typeof(byte[])) */ {
                    return KeyData;
                }
            }
        }

        public static implicit operator TangleKey (string key) {
            return new TangleKey(key);
        }

        public override string ToString () {
            var value = Value;

            if (value is ArraySegment<byte>) {
                var sb = new StringBuilder();
                for (int i = 0; i < KeyData.Count; i++)
                    sb.AppendFormat("{0:X2}", KeyData.Array[i + KeyData.Offset]);
                return sb.ToString();
            } else {
                return value.ToString();
            }
        }
    }

    /// <summary>
    /// Represents a persistent dictionary keyed by arbitrary byte strings. The values are not stored in any given order on disk, and the values are not required to be resident in memory.
    /// At any given time a portion of the Tangle's values may be resident in memory. If a value is not resident in memory, it will be fetched asynchronously from disk.
    /// The Tangle's keys are implicitly ordered, which allows for efficient lookups of individual values by key.
    /// Converting values to/from their disk format is handled by the provided TangleSerializer and TangleDeserializer.
    /// The Tangle's disk storage engine partitions its storage up into pages based on the provided page size. For optimal performance, this should be an integer multiple of the size of a memory page (typically 4KB).
    /// </summary>
    /// <typeparam name="T">The type of the value stored within the tangle.</typeparam>
    public unsafe class Tangle<T> : IDisposable {
        public static TangleSerializer<T> DefaultSerializer;
        public static TangleDeserializer<T> DefaultDeserializer;

        static Tangle () {
            DefaultDeserializer = (Stream i, out T o) => {
                var ser = new XmlSerializer(typeof(T));
                var temp = ser.Deserialize(i);
                o = (T)temp;
            };
            DefaultSerializer = (ref T i, Stream o) => {
                var ser = new XmlSerializer(typeof(T));
                ser.Serialize(o, i);
            };
        }

        public const string ExplanatoryPlaceholderText =
@"This is a DataMangler database. 
All the actual data is stored in NTFS streams attached to this file.
For more information, see http://support.microsoft.com/kb/105763.";

        public const bool TraceKeyInsertions = false;

        public const uint CurrentFormatVersion = 1;

        public readonly string Filename;
        public readonly TaskScheduler Scheduler;
        public readonly TangleSerializer<T> Serializer;
        public readonly TangleDeserializer<T> Deserializer;

        protected readonly ReaderWriterLockSlim IndexLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

        protected struct WorkItem {
            public IFuture Future;
            public Action Action;
        }

        protected Squared.Task.Internal.WorkerThread<ConcurrentQueue<WorkItem>> _WorkerThread;

        private readonly StreamRef IndexStream;
        private readonly StreamRef KeyStream;
        private readonly StreamRef DataStream;

        public Tangle (
            TaskScheduler scheduler, string filename, 
            TangleSerializer<T> serializer = null, 
            TangleDeserializer<T> deserializer = null
        ) {
            Scheduler = scheduler;
            Filename = filename;
            Serializer = serializer ?? DefaultSerializer;
            Deserializer = deserializer ?? DefaultDeserializer;

            File.WriteAllText(Filename, ExplanatoryPlaceholderText);

            IndexStream = new StreamRef(Filename, "index");
            KeyStream = new StreamRef(Filename, "keys");
            DataStream = new StreamRef(Filename, "data");

            VersionCheck(IndexStream);
            VersionCheck(KeyStream);
            VersionCheck(DataStream);
        }

        private static void VersionCheck (StreamRef stream) {
            var streamVersion = stream.FormatVersion;
            if (streamVersion == 0) {
                stream.FormatVersion = CurrentFormatVersion;
            } else if (streamVersion != CurrentFormatVersion) {
                throw new NotImplementedException("Format upgrade not implemented");
            }
        }

        private static long StreamAppend<U> (StreamRef stream, ref U value) 
            where U : struct {
            uint entrySize = (uint)Marshal.SizeOf(typeof(U));
            long spot = stream.AllocateSpace(entrySize);

            using (var range = stream.AccessRange(spot, entrySize, MemoryMappedFileAccess.Write))
                Unsafe<U>.StructureToPtr(ref value, range.Pointer, entrySize);

            return spot;
        }

        private static long StreamAppend (StreamRef stream, ArraySegment<byte> data) {
            long spot = stream.AllocateSpace((uint)data.Count);

            using (var range = stream.AccessRange(spot, (uint)data.Count, MemoryMappedFileAccess.Write))
                WriteBytes(range, 0, data);

            return spot;
        }

        /// <summary>
        /// Reads a value from the tangle, looking it up via its key.
        /// </summary>
        /// <returns>A future that will contain the value once it has been read.</returns>
        public Future<T> Get (TangleKey key) {
            var f = new Future<T>();
            QueueWorkItem(f, () => {
                T result;
                if (InternalGet(key, out result))
                    f.SetResult(result, null);
                else
                    f.SetResult(result, new KeyNotFoundException(key.ToString()));
            });
            return f;
        }

        /// <summary>
        /// Stores a value into the tangle, assigning it a given key.
        /// </summary>
        /// <returns>A future that completes once the value has been stored to disk.</returns>
        public IFuture Set (TangleKey key, T value) {
            var f = new SignalFuture();
            QueueWorkItem(f, () => {
                InternalSet(key, value);
                f.Complete();
            });
            return f;
        }

        private void QueueWorkItem (IFuture future, Action action) {
            if (_WorkerThread == null)
                _WorkerThread = new Squared.Task.Internal.WorkerThread<ConcurrentQueue<WorkItem>>(WorkerThreadFunc, ThreadPriority.Normal);

            _WorkerThread.WorkItems.Enqueue(new WorkItem {
                Future = future,
                Action = action
            });

            _WorkerThread.Wake();
        }

        protected void WorkerThreadFunc (ConcurrentQueue<WorkItem> workItems, ManualResetEvent newWorkItemEvent) {
            while (true) {
                WorkItem item;
                while (workItems.TryDequeue(out item)) {
                    try {
                        item.Action();
                    } catch (Exception ex) {
                        item.Future.Fail(ex);
                    }
                }

                newWorkItemEvent.WaitOne();
                newWorkItemEvent.Reset();
            }
        }

        private unsafe int CompareKeys (byte * lhs, uint lengthLhs, byte * rhs, uint lengthRhs) {
            uint compareLength = Math.Min(lengthLhs, lengthRhs);

            for (uint i = 0; i < compareLength; i++) {
                int delta = lhs[i] - rhs[i];
                if (delta != 0)
                    return Math.Sign(delta);
            }

            if (lengthLhs > compareLength)
                return 1;
            else if (lengthRhs > compareLength)
                return -1;
            else
                return 0;
        }

        private StreamRange AccessIndex (long index) {
            long count = (IndexStream.Length / IndexEntry.Size);

            if ((index < 0) || (index >= count))
                throw new ArgumentException(String.Format(
                    "Expected 0 <= index < {0}, but index was {1}", count, index
                ), "index");

            long position = index * IndexEntry.Size;
            return IndexStream.AccessRange(position, IndexEntry.Size, MemoryMappedFileAccess.Read);
        }

        private unsafe bool IndexEntryByKey (long firstIndex, long indexCount, ref TangleKey rhs, out IndexEntry resultEntry, out long resultIndex) {
            uint lengthRhs = (uint)rhs.KeyData.Count;
            long min = firstIndex, max = firstIndex + indexCount - 1;
            long pivot;
            int delta = 0;

            fixed (byte * pRhs = &rhs.KeyData.Array[rhs.KeyData.Offset])
            while (min <= max) {

                pivot = min + ((max - min) >> 1);

                using (var indexRange = AccessIndex(pivot)) {
                    var pEntry = (IndexEntry *)indexRange.Pointer;
                    if (pEntry->IsValid != 1)
                        throw new InvalidDataException();

                    using (var keyRange = KeyStream.AccessRange(
                        pEntry->KeyOffset, pEntry->KeyLength, MemoryMappedFileAccess.Read
                    )) {
                        var pLhs = keyRange.Pointer;
                        delta = CompareKeys(pLhs, pEntry->KeyLength, pRhs, lengthRhs);
                    }

                    if (delta == 0) {
                        resultEntry = *pEntry;
                        resultIndex = pivot;

                        return true;
                    } else if (delta < 0) {
                        min = pivot + 1;
                    } else {
                        max = pivot - 1;
                    }
                }
            }

            resultEntry = default(IndexEntry);
            resultIndex = min;

            return false;
        }

        private long IndexCount {
            get {
                return (IndexStream.Length / IndexEntry.Size);
            }
        }

        private bool IndexEntryByKey (ref TangleKey key, out IndexEntry resultEntry, out long resultIndex) {
            return IndexEntryByKey(0, IndexCount, ref key, out resultEntry, out resultIndex);
        }

        private void ReadValue (ref IndexEntry entry, out T value) {
            using (var range = DataStream.AccessRange(entry.DataOffset, entry.DataLength))
            using (var ms = new UnmanagedMemoryStream(range.Pointer, entry.DataLength, entry.DataLength, FileAccess.Read))
                Deserializer(ms, out value);
        }

        private static void ReadBytes (StreamRange range, long offset, byte[] buffer, long bufferOffset, uint count) {
            var ptr = range.Pointer;
            for (uint i = 0; i < count; i++)
                buffer[i + bufferOffset] = ptr[i + offset];
        }

        private static void WriteBytes (StreamRange range, long offset, ArraySegment<byte> bytes) {
            var ptr = range.Pointer;
            for (uint i = 0; i < bytes.Count; i++)
                ptr[i + offset] = bytes.Array[i + bytes.Offset];
        }

        private static void ZeroBytes (StreamRange range, long offset, uint count) {
            var ptr = range.Pointer;
            for (uint i = 0; i < count; i++)
                ptr[i + offset] = 0;
        }

        private unsafe void MoveEntriesForward (long positionToClear, long emptyPosition) {
            long size = emptyPosition - positionToClear + IndexEntry.Size;
            long position = size - (IndexEntry.Size * 2);

            using (var range = IndexStream.AccessRange(
                positionToClear, (uint)size,
                MemoryMappedFileAccess.ReadWrite
            )) {
                var ptr = range.Pointer;

                while (position >= 0) {
                    IndexEntry* pSource = (IndexEntry *)(ptr + position);
                    IndexEntry* pDest = (IndexEntry*)(ptr + position + IndexEntry.Size);

                    if (pSource->IsValid != 1)
                        throw new InvalidDataException();
                    pSource->IsValid = 0;

                    *pDest = *pSource;

                    pDest->IsValid = 1;

                    position -= IndexEntry.Size;
                }
            }
        }

        private unsafe void InternalSet (TangleKey key, T value) {
            const int writeModeNew = 0;
            const int writeModeReplaceData = 1;
            const int writeModeNewData = 2;
            const int writeModeInsertNew = 3;

            int writeMode;
            IndexEntry indexEntry;
            long offset, dataOffset = 0, keyOffset = 0;
            long positionToClear = 0, emptyPosition = 0;

            using (var ms = new MemoryStream()) {
                Serializer(ref value, ms);

                long index, count = IndexCount;
                if (!IndexEntryByKey(0, count, ref key, out indexEntry, out index)) {
                    offset = index * IndexEntry.Size;
                    var newSpot = IndexStream.AllocateSpace(IndexEntry.Size);

                    if (index < count) {
                        writeMode = writeModeInsertNew;
                        positionToClear = offset;
                        emptyPosition = newSpot;
                    } else {
                        writeMode = writeModeNew;
                        offset = newSpot;
                    }
                } else if (ms.Length > indexEntry.DataLength) {
                    offset = index * IndexEntry.Size;
                    writeMode = writeModeNewData;
                } else {
                    offset = index * IndexEntry.Size;
                    writeMode = writeModeReplaceData;
                }

                if (writeMode == writeModeInsertNew || writeMode == writeModeNew)
                    keyOffset = KeyStream.AllocateSpace((uint)key.KeyData.Count);
                if (writeMode != writeModeReplaceData)
                    dataOffset = DataStream.AllocateSpace((uint)ms.Length);

                if (writeMode == writeModeInsertNew)
                    MoveEntriesForward(positionToClear, emptyPosition);

                using (var indexRange = IndexStream.AccessRange(offset, IndexEntry.Size, MemoryMappedFileAccess.ReadWrite)) {
                    var pEntry = (IndexEntry *)indexRange.Pointer;

                    if (writeMode == writeModeNew || writeMode == writeModeInsertNew) {
                        *pEntry = new IndexEntry {
                            DataOffset = (uint)dataOffset,
                            KeyOffset = (uint)keyOffset,
                            DataLength = (uint)ms.Length,
                            KeyLength = (ushort)key.KeyData.Count,
                            KeyType = key.OriginalTypeId,
                            IsValid = 0
                        };

                        using (var keyRange = KeyStream.AccessRange(pEntry->KeyOffset, pEntry->KeyLength, MemoryMappedFileAccess.Write))
                            WriteBytes(keyRange, 0, key.KeyData);

                    } else {
                        pEntry->IsValid = 0;
                    }

                    var segment = ms.GetSegment();

                    if (writeMode == writeModeInsertNew) {
                        using (var range = DataStream.AccessRange(pEntry->DataOffset, pEntry->DataLength, MemoryMappedFileAccess.Write))
                            ZeroBytes(range, 0, pEntry->DataLength);
                    }

                    if (writeMode == writeModeNewData || writeMode == writeModeInsertNew) {
                        pEntry->DataOffset = (uint)dataOffset;
                        pEntry->DataLength = (uint)segment.Count;
                    }

                    using (var range = DataStream.AccessRange(pEntry->DataOffset, pEntry->DataLength, MemoryMappedFileAccess.Write)) {
                        WriteBytes(range, 0, segment);

                        var bytesToZero = (uint)(pEntry->DataLength - segment.Count);
                        if (bytesToZero > 0)
                            ZeroBytes(range, segment.Count, (uint)bytesToZero);
                    }

                    pEntry->DataLength = (uint)segment.Count;

                    pEntry->IsValid = 1;
                }
            }

            if (TraceKeyInsertions) {
                Console.WriteLine("--------");
                foreach (var k in Keys)
                    Console.WriteLine(k.Value);
            }
        }

        private unsafe TangleKey GetKeyFromIndex (long index) {
            using (var indexRange = AccessIndex(index)) {
                var pEntry = (IndexEntry *)indexRange.Pointer;
                if (pEntry->IsValid != 1)
                    throw new InvalidDataException();

                using (var keyRange = KeyStream.AccessRange(
                    pEntry->KeyOffset, pEntry->KeyLength, MemoryMappedFileAccess.Read
                )) {
                    var array = new byte[pEntry->KeyLength];
                    ReadBytes(keyRange, 0, array, 0, pEntry->KeyLength); 

                    return new TangleKey(array, pEntry->KeyType);
                }
            }
        }

        public IEnumerable<TangleKey> Keys {
            get {
                long count = IndexStream.Length / IndexEntry.Size;
                for (long i = 0; i < count; i++)
                    yield return GetKeyFromIndex(i);
            }
        }

        private bool InternalGet (TangleKey key, out T value) {
            IndexEntry indexEntry;
            long temp;

            if (!IndexEntryByKey(ref key, out indexEntry, out temp)) {
                value = default(T);
                return false;
            }

            ReadValue(ref indexEntry, out value);
            return true;
        }

        /// <summary>
        /// Exports the contents of each of the database's streams to a folder, for easier debugging.
        /// </summary>
        public void ExportStreams (string destinationFolder) {
            if (!Directory.Exists(destinationFolder))
                Directory.CreateDirectory(destinationFolder);

            ExportStream(IndexStream, Path.Combine(destinationFolder, "index"));
            ExportStream(KeyStream, Path.Combine(destinationFolder, "keys"));
            ExportStream(DataStream, Path.Combine(destinationFolder, "data"));
        }

        private void ExportStream (StreamRef stream, string file) {
            if (File.Exists(file))
                File.Delete(file);
            using (var fs = File.OpenWrite(file)) {
                var headerBuf = new byte[StreamRef.HeaderSize];
                using (var access = stream.AccessHeader())
                    Marshal.Copy(new IntPtr(access.Ptr), headerBuf, 0, (int)StreamRef.HeaderSize);
                fs.Write(headerBuf, 0, (int)StreamRef.HeaderSize);

                var size = (int)stream.Length;
                var buffer = new byte[size];
                using (var access = stream.AccessRange(0, (uint)size, MemoryMappedFileAccess.Read))
                    ReadBytes(access, 0, buffer, 0, (uint)size);
                fs.Write(buffer, 0, size);
            }
        }

        public IEnumerator<object> Dispose () {
            IndexStream.Dispose();
            DataStream.Dispose();
            KeyStream.Dispose();

            yield break;
        }

        void IDisposable.Dispose () {
            Scheduler.WaitFor(Dispose());
        }
    }
}