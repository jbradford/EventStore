﻿// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.IO;
using System.Text;
using EventStore.Common.Utils;
using EventStore.Core.Data;
using EventStore.Core.DataStructures;
using EventStore.Core.Index;
using EventStore.Core.Services;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Tests.Fakes;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage
{
    public abstract class ReadIndexTestScenario : SpecificationWithDirectoryPerTestFixture
    {
        protected readonly int MaxEntriesInMemTable;
        protected TableIndex TableIndex;
        protected IReadIndex ReadIndex;

        protected TFChunkDb Db;
        protected TFChunkWriter Writer;
        protected ICheckpoint WriterChecksum;
        protected ICheckpoint ChaserChecksum;

        private TFChunkScavenger _scavenger;
        private bool _scavenge;
        private bool _completeLastChunkOnScavenge;

        protected ReadIndexTestScenario(int maxEntriesInMemTable = 20)
        {
            Ensure.Positive(maxEntriesInMemTable, "maxEntriesInMemTable");
            MaxEntriesInMemTable = maxEntriesInMemTable;
        }

        public override void TestFixtureSetUp()
        {
            base.TestFixtureSetUp();

            WriterChecksum = new InMemoryCheckpoint(0);
            ChaserChecksum = new InMemoryCheckpoint(0);

            Db = new TFChunkDb(new TFChunkDbConfig(PathName,
                                                   new VersionedPatternFileNamingStrategy(PathName, "chunk-"),
                                                   10000,
                                                   0,
                                                   WriterChecksum,
                                                   ChaserChecksum,
                                                   new[] { WriterChecksum, ChaserChecksum }));

            Db.OpenVerifyAndClean();
            // create db
            Writer = new TFChunkWriter(Db);
            Writer.Open();
            WriteTestScenario();
            Writer.Close();
            Writer = null;

            WriterChecksum.Flush();
            ChaserChecksum.Write(WriterChecksum.Read());
            ChaserChecksum.Flush();

            TableIndex = new TableIndex(GetFilePathFor("index"),
                                        () => new HashListMemTable(MaxEntriesInMemTable * 2),
                                        MaxEntriesInMemTable);

            var reader = new TFChunkReader(Db, Db.Config.WriterCheckpoint);
            ReadIndex = new ReadIndex(new NoopPublisher(),
                                      2,
                                      () => new TFChunkSequentialReader(Db, Db.Config.WriterCheckpoint, 0),
                                      () => reader,
                                      TableIndex,
                                      new ByLengthHasher(),
                                      new NoLRUCache<string, StreamCacheInfo>());

            ReadIndex.Build();

            // scavenge must run after readIndex is built
            if (_scavenge)
            {
                if (_completeLastChunkOnScavenge)
                    Db.Manager.GetChunk(Db.Manager.ChunksCount - 1).Complete();
                _scavenger = new TFChunkScavenger(Db, ReadIndex);
                _scavenger.Scavenge(alwaysKeepScavenged: true);
            }
        }

        public override void TestFixtureTearDown()
        {
            ReadIndex.Close();
            ReadIndex.Dispose();

            TableIndex.Close();

            Db.Close();
            Db.Dispose();

            base.TestFixtureTearDown();
        }

        protected abstract void WriteTestScenario();

        protected EventRecord WriteStreamCreated(string eventStreamId,
                                                 string metadata = null, 
                                                 DateTime? timestamp = null,
                                                 Guid eventId = default(Guid))
        {
            var logPosition = WriterChecksum.ReadNonFlushed();
            var rec = LogRecord.Prepare(logPosition,
                                        eventId == default(Guid) ? Guid.NewGuid() : eventId,
                                        Guid.NewGuid(),
                                        logPosition,
                                        0,
                                        eventStreamId,
                                        ExpectedVersion.NoStream,
                                        PrepareFlags.Data | PrepareFlags.IsJson | PrepareFlags.TransactionBegin | PrepareFlags.TransactionEnd,
                                        SystemEventTypes.StreamCreated,
                                        LogRecord.NoData,
                                        metadata == null ? LogRecord.NoData : Encoding.UTF8.GetBytes(metadata),
                                        timestamp);

            long pos;
            Assert.IsTrue(Writer.Write(rec, out pos));

            var commit = LogRecord.Commit(WriterChecksum.ReadNonFlushed(), rec.CorrelationId, rec.LogPosition, 0);
            Assert.IsTrue(Writer.Write(commit, out pos));

            var eventRecord = new EventRecord(0, rec);
            return eventRecord;
        }

        protected EventRecord WriteSingleEvent(string eventStreamId, 
                                               int eventNumber, 
                                               string data,
                                               DateTime? timestamp = null,
                                               Guid eventId = default(Guid),
                                               bool retryOnFail = false)
        {
            var prepare = LogRecord.SingleWrite(WriterChecksum.ReadNonFlushed(),
                                                eventId == default(Guid) ? Guid.NewGuid() : eventId,
                                                Guid.NewGuid(),
                                                eventStreamId,
                                                eventNumber - 1,
                                                "some-type",
                                                Encoding.UTF8.GetBytes(data),
                                                null,
                                                timestamp);
            long pos;

            if (!retryOnFail)
            {
                Assert.IsTrue(Writer.Write(prepare, out pos));
            }
            else
            {
                long firstPos = prepare.LogPosition;
                if (!Writer.Write(prepare, out pos))
                {
                    prepare = LogRecord.SingleWrite(pos,
                                                    prepare.CorrelationId,
                                                    prepare.EventId,
                                                    prepare.EventStreamId,
                                                    prepare.ExpectedVersion,
                                                    prepare.EventType,
                                                    prepare.Data,
                                                    prepare.Metadata,
                                                    prepare.TimeStamp);
                    if (!Writer.Write(prepare, out pos))
                        Assert.Fail("Second write try failed when first writing prepare at {0}, then at {1}.", firstPos, prepare.LogPosition);
                }
            }

            var commit = LogRecord.Commit(WriterChecksum.ReadNonFlushed(), prepare.CorrelationId, prepare.LogPosition, eventNumber);
            Assert.IsTrue(Writer.Write(commit, out pos));

            var eventRecord = new EventRecord(eventNumber, prepare);
            return eventRecord;
        }

        protected EventRecord WriteTransactionBegin(string eventStreamId, int expectedVersion, int eventNumber, string eventData)
        {
            var prepare = LogRecord.Prepare(WriterChecksum.ReadNonFlushed(),
                                            Guid.NewGuid(),
                                            Guid.NewGuid(),
                                            WriterChecksum.ReadNonFlushed(),
                                            0,
                                            eventStreamId,
                                            expectedVersion,
                                            PrepareFlags.Data | PrepareFlags.TransactionBegin,
                                            "some-type",
                                            Encoding.UTF8.GetBytes(eventData),
                                            null);
            long pos;
            Assert.IsTrue(Writer.Write(prepare, out pos));
            return new EventRecord(eventNumber, prepare);
        }

        protected PrepareLogRecord WriteTransactionBegin(string eventStreamId, int expectedVersion)
        {
            var prepare = LogRecord.TransactionBegin(WriterChecksum.ReadNonFlushed(), Guid.NewGuid(), eventStreamId, expectedVersion);
            long pos;
            Assert.IsTrue(Writer.Write(prepare, out pos));
            return prepare;
        }

        protected EventRecord WriteTransactionEvent(Guid correlationId,
                                                    long transactionPos,
                                                    int transactionOffset,
                                                    string eventStreamId,
                                                    int eventNumber,
                                                    string eventData,
                                                    PrepareFlags flags,
                                                    bool retryOnFail = false)
        {
            var prepare = LogRecord.Prepare(WriterChecksum.ReadNonFlushed(),
                                            correlationId,
                                            Guid.NewGuid(),
                                            transactionPos,
                                            transactionOffset,
                                            eventStreamId,
                                            ExpectedVersion.Any,
                                            flags,
                                            "some-type",
                                            Encoding.UTF8.GetBytes(eventData),
                                            null);

            if (retryOnFail)
            {
                long firstPos = prepare.LogPosition;
                long newPos;
                if (!Writer.Write(prepare, out newPos))
                {
                    var tPos = prepare.TransactionPosition == prepare.LogPosition ? newPos : prepare.TransactionPosition;
                    prepare = new PrepareLogRecord(newPos,
                                                   prepare.CorrelationId,
                                                   prepare.EventId,
                                                   tPos,
                                                   prepare.TransactionOffset,
                                                   prepare.EventStreamId,
                                                   prepare.ExpectedVersion,
                                                   prepare.TimeStamp,
                                                   prepare.Flags,
                                                   prepare.EventType,
                                                   prepare.Data,
                                                   prepare.Metadata);
                    if (!Writer.Write(prepare, out newPos))
                        Assert.Fail("Second write try failed when first writing prepare at {0}, then at {1}.", firstPos, prepare.LogPosition);
                }
                return new EventRecord(eventNumber, prepare);
            }

            long pos;
            Assert.IsTrue(Writer.Write(prepare, out pos));
            return new EventRecord(eventNumber, prepare);
        }

        protected PrepareLogRecord WriteTransactionEnd(Guid correlationId, long transactionId, string eventStreamId)
        {
            var prepare = LogRecord.TransactionEnd(WriterChecksum.ReadNonFlushed(),
                                                   correlationId,
                                                   Guid.NewGuid(),
                                                   transactionId,
                                                   eventStreamId);
            long pos;
            Assert.IsTrue(Writer.Write(prepare, out pos));
            return prepare;
        }

        protected PrepareLogRecord WritePrepare(string streamId, 
                                                int expectedVersion, 
                                                Guid eventId = default(Guid), 
                                                string eventType = null, 
                                                string data = null)
        {
            long pos;
            var prepare = LogRecord.SingleWrite(WriterChecksum.ReadNonFlushed(),
                                                eventId == default(Guid) ? Guid.NewGuid() : eventId,
                                                Guid.NewGuid(),
                                                streamId,
                                                expectedVersion,
                                                eventType.IsEmptyString() ? "some-type" : eventType,
                                                data.IsEmptyString() ? LogRecord.NoData : Encoding.UTF8.GetBytes(data),
                                                LogRecord.NoData,
                                                DateTime.UtcNow);
            Assert.IsTrue(Writer.Write(prepare, out pos));

            return prepare;
        }

        protected CommitLogRecord WriteCommit(long preparePos, string eventStreamId, int eventNumber)
        {
            var commit = LogRecord.Commit(WriterChecksum.ReadNonFlushed(), Guid.NewGuid(), preparePos, eventNumber);
            long pos;
            Assert.IsTrue(Writer.Write(commit, out pos));
            return commit;
        }

        protected long WriteCommit(Guid correlationId, long transactionId, string eventStreamId, int eventNumber)
        {
            var commit = LogRecord.Commit(WriterChecksum.ReadNonFlushed(), correlationId, transactionId, eventNumber);
            long pos;
            Assert.IsTrue(Writer.Write(commit, out pos));
            return commit.LogPosition;
        }

        protected EventRecord WriteDelete(string eventStreamId)
        {
            var prepare = LogRecord.DeleteTombstone(WriterChecksum.ReadNonFlushed(),
                                                           Guid.NewGuid(),
                                                           eventStreamId,
                                                           ExpectedVersion.Any);
            long pos;
            Assert.IsTrue(Writer.Write(prepare, out pos));
            var commit = LogRecord.Commit(WriterChecksum.ReadNonFlushed(),
                                          prepare.CorrelationId,
                                          prepare.LogPosition,
                                          EventNumber.DeletedStream);
            Assert.IsTrue(Writer.Write(commit, out pos));

            return new EventRecord(EventNumber.DeletedStream, prepare);
        }

        protected TFPos GetBackwardReadPos()
        {
            var pos = new TFPos(WriterChecksum.ReadNonFlushed(), WriterChecksum.ReadNonFlushed());
            return pos;
        }

        protected void Scavenge(bool completeLast)
        {
            if (_scavenge)
                throw new InvalidOperationException("Scavenge can be executed only once in ReadIndexTestScenario");
            _scavenge = true;
            _completeLastChunkOnScavenge = completeLast;
        }
    }
}