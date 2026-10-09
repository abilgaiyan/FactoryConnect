using System.Data;
using System.Globalization;
using FactoryConnect.Abstractions;
using Microsoft.Data.SqlClient;

namespace FactoryConnect.Persistence.SqlServer;

internal sealed class SqlServerOperationalMetricCoverageAssessmentStore : IOperationalMetricCoverageAssessmentStore
{
    private readonly string _connectionString;
    private readonly Func<byte[], byte[]> _hash;
    private readonly Action<string>? _publicationStage;

    public SqlServerOperationalMetricCoverageAssessmentStore(string connectionString)
        : this(connectionString, OperationalMetricCoverageV1Codec.Hash, null) { }

    internal SqlServerOperationalMetricCoverageAssessmentStore(string connectionString,
        Func<byte[], byte[]> hash, Action<string>? publicationStage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(hash);
        _connectionString = connectionString;
        _hash = hash;
        _publicationStage = publicationStage;
    }

    public async ValueTask<OperationalMetricCoveragePublicationOutcome> PublishAsync(
        OperationalMetricCoveragePublicationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = OperationalMetricCoverageV1Codec.EncodeSubject(request.Subject);
        var content = OperationalMetricCoverageV1Codec.EncodeContent(request.Assessment);
        var hash = GetHash(identity);
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            _publicationStage?.Invoke("SubjectLockRequested");
            await AcquireAsync(connection, transaction, hash, "Exclusive", cancellationToken);
            _publicationStage?.Invoke("SubjectLockAcquired");
            var source = await ResolveSourceAsync(connection, transaction, request.Subject, cancellationToken);
            if (source is null) { return OperationalMetricCoveragePublicationOutcome.SourceRevisionAbsent; }
            if (!source.Valid) { return OperationalMetricCoveragePublicationOutcome.IntegrityFailure; }
            var stored = await InspectAsync(connection, transaction, request.Subject, identity, hash, request.ProposedRevision.Value, cancellationToken);
            if (stored.Outcome == OperationalMetricCoverageReadOutcome.UnsupportedEncoding)
            {
                return OperationalMetricCoveragePublicationOutcome.UnsupportedEncoding;
            }

            if (stored.Outcome == OperationalMetricCoverageReadOutcome.Corrupt)
            {
                return OperationalMetricCoveragePublicationOutcome.IntegrityFailure;
            }

            var decision = ClassifyProposal(stored.ProposedContent, content, stored.Head, request.ExpectedPredecessor?.Value);
            if (decision is { } established) { return established; }

            var subjectId = stored.SubjectId;
            if (subjectId is null)
            {
                await using var insert = Command(connection, transaction, """
                    INSERT INTO dbo.OperationalMetricCoverageSubject
                        (SubjectHash, IdentityCodecVersion, SubjectBinary, MetricAggregationProcessorRowId, MetricInputStreamRowId, SourcePosition)
                    OUTPUT INSERTED.SubjectId
                    VALUES (@Hash, 1, @Identity, @Processor, @Stream, @Position);
                    """);
                AddBytes(insert, "@Hash", hash, 32);
                AddBytes(insert, "@Identity", identity);
                insert.Parameters.Add("@Processor", SqlDbType.BigInt).Value = source.Processor;
                insert.Parameters.Add("@Stream", SqlDbType.BigInt).Value = source.Stream;
                insert.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", request.Subject.SourceRevision.Position.Value));
                subjectId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                _publicationStage?.Invoke("SubjectInserted");
            }

            await using (var insert = Command(connection, transaction, """
                INSERT INTO dbo.OperationalMetricCoverageVersion (SubjectId, AssessmentRevision, ContentCodecVersion, ContentBinary)
                VALUES (@Subject, @Revision, 1, @Content);
                """))
            {
                AddVersionKey(insert, subjectId.Value, request.ProposedRevision.Value);
                AddBytes(insert, "@Content", content);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            _publicationStage?.Invoke("VersionInserted");
            await using (var head = Command(connection, transaction, stored.Head is null
                ? "INSERT INTO dbo.OperationalMetricCoverageHead (SubjectId, HeadRevision) VALUES (@Subject, @Revision);"
                : "UPDATE dbo.OperationalMetricCoverageHead SET HeadRevision = @Revision WHERE SubjectId = @Subject AND HeadRevision = @Expected;"))
            {
                AddVersionKey(head, subjectId.Value, request.ProposedRevision.Value);
                if (stored.Head is { } previous) { head.Parameters.Add("@Expected", SqlDbType.BigInt).Value = previous; }
                if (await head.ExecuteNonQueryAsync(cancellationToken) != 1) { throw new InvalidDataException("Coverage head changed under subject serialization."); }
            }

            _publicationStage?.Invoke("HeadAdvanced");
            await transaction.CommitAsync(cancellationToken);
            _publicationStage?.Invoke("CommitAcknowledged");
            return OperationalMetricCoveragePublicationOutcome.Appended;
        }
        catch (Exception exception) when (IsCanceledOperationalFailure(exception, cancellationToken))
        {
            throw new OperationCanceledException("Coverage publication was canceled; its commit outcome may be uncertain.", exception, cancellationToken);
        }
    }

    public async ValueTask<OperationalMetricCoverageReadResult> ReadExactAsync(OperationalMetricCoverageSubject subject,
        OperationalMetricCoverageRevision revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(revision);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = OperationalMetricCoverageV1Codec.EncodeSubject(subject);
        var hash = GetHash(identity);
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            _publicationStage?.Invoke("SubjectLockRequested");
            await AcquireAsync(connection, transaction, hash, "Shared", cancellationToken);
            _publicationStage?.Invoke("SubjectLockAcquired");
            var stored = await InspectAsync(connection, transaction, subject, identity, hash, revision.Value, cancellationToken);
            var result = stored.Assessment is null
                ? OperationalMetricCoverageReadResult.WithoutAssessment(stored.Outcome)
                : OperationalMetricCoverageReadResult.Found(stored.Assessment);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception) when (IsCanceledOperationalFailure(exception, cancellationToken))
        {
            throw new OperationCanceledException("Coverage read was canceled.", exception, cancellationToken);
        }
        catch (SqlException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationalMetricCoverageReadResult.WithoutAssessment(OperationalMetricCoverageReadOutcome.Unavailable);
        }
        catch (CoverageLockException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationalMetricCoverageReadResult.WithoutAssessment(OperationalMetricCoverageReadOutcome.Unavailable);
        }
    }

    internal static OperationalMetricCoveragePublicationOutcome? ClassifyProposal(byte[]? existing, byte[] proposed,
        long? head, long? expectedPredecessor)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        // Never compute head + 1 here: request validation already checked the proposed successor.
        // Exact historical replay works at long.MaxValue and after subsequent publications.
        if (existing is not null)
        {
            return existing.AsSpan().SequenceEqual(proposed)
                ? OperationalMetricCoveragePublicationOutcome.Replayed
                : OperationalMetricCoveragePublicationOutcome.ContentConflict;
        }

        return head != expectedPredecessor ? OperationalMetricCoveragePublicationOutcome.PredecessorConflict : null;
    }

    private static bool IsCanceledOperationalFailure(Exception exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested && exception is SqlException or CoverageLockException;

    private byte[] GetHash(byte[] identity)
    {
        var hash = _hash(identity);
        if (hash.Length != 32) { throw new InvalidOperationException("Coverage subject hash must contain 32 bytes."); }
        return hash;
    }

    private static async Task AcquireAsync(SqlConnection connection, SqlTransaction transaction, byte[] hash,
        string mode, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            DECLARE @Code int;
            EXEC @Code = sys.sp_getapplock @Resource = @Resource, @LockMode = @Mode,
                @LockOwner = N'Transaction', @LockTimeout = 30000, @DbPrincipal = N'public';
            SELECT @Code;
            """);
        command.CommandTimeout = 0;
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = "FactoryConnect.CoverageAssessment:" + Convert.ToHexString(hash);
        command.Parameters.Add("@Mode", SqlDbType.NVarChar, 32).Value = mode;
        if (await command.ExecuteScalarAsync(cancellationToken) is not int code || code < 0)
        {
            throw new CoverageLockException();
        }
    }

    private static async Task<SourceBinding?> ResolveSourceAsync(SqlConnection connection, SqlTransaction transaction,
        OperationalMetricCoverageSubject subject, CancellationToken cancellationToken)
    {
        var source = subject.SourceRevision;
        // Existing durable source keys have a 256-code-unit capacity. Longer I1 subjects
        // remain encodable, but cannot resolve to an existing durable source binding.
        if (source.ProcessorId.Value.Length > OrdinalStringKeyCodec.MaxCodeUnits
            || source.StreamId.StreamKey.Length > OrdinalStringKeyCodec.MaxCodeUnits) { return null; }
        await using var command = Command(connection, transaction, """
            SELECT p.MetricAggregationProcessorRowId, p.MetricInputStreamRowId,
                p.ProcessorKey, p.ProcessorKeyBinary, s.MachineId, s.StreamKey, s.StreamKeyBinary
            FROM dbo.MetricAggregationProcessor p
            INNER JOIN dbo.MetricInputStream s ON s.MetricInputStreamRowId = p.MetricInputStreamRowId
            INNER JOIN dbo.MetricAggregationRevision r ON r.MetricAggregationProcessorRowId = p.MetricAggregationProcessorRowId
            WHERE p.ProcessorKeyBinary = @Processor AND s.MachineId = @Machine
                AND s.StreamKeyBinary = @Stream AND r.Position = @Position;
            """);
        AddBytes(command, "@Processor", OrdinalStringKeyCodec.Encode(source.ProcessorId.Value), 512);
        AddBytes(command, "@Stream", OrdinalStringKeyCodec.Encode(source.StreamId.StreamKey), 512);
        command.Parameters.Add("@Machine", SqlDbType.UniqueIdentifier).Value = source.StreamId.MachineId.Value;
        command.Parameters.Add(SqlServerUInt64.CreateParameter("@Position", source.Position.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) { return null; }
        var valid = string.Equals(reader.GetString(2), source.ProcessorId.Value, StringComparison.Ordinal)
            && ((byte[])reader[3]).AsSpan().SequenceEqual(OrdinalStringKeyCodec.Encode(reader.GetString(2)))
            && reader.GetGuid(4) == source.StreamId.MachineId.Value
            && string.Equals(reader.GetString(5), source.StreamId.StreamKey, StringComparison.Ordinal)
            && ((byte[])reader[6]).AsSpan().SequenceEqual(OrdinalStringKeyCodec.Encode(reader.GetString(5)));
        return new SourceBinding(reader.GetInt64(0), reader.GetInt64(1), valid);
    }

    private static async Task<StoredState> InspectAsync(SqlConnection connection, SqlTransaction transaction,
        OperationalMetricCoverageSubject subject, byte[] identity, byte[] hash, long revision, CancellationToken cancellationToken)
    {
        long subjectId;
        long processor;
        long stream;
        decimal position;
        byte[] storedIdentity;
        await using (var command = Command(connection, transaction, """
            SELECT SubjectId, IdentityCodecVersion, SubjectBinary, MetricAggregationProcessorRowId,
                MetricInputStreamRowId, SourcePosition FROM dbo.OperationalMetricCoverageSubject WHERE SubjectHash = @Hash;
            """))
        {
            AddBytes(command, "@Hash", hash, 32);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) { return new(OperationalMetricCoverageReadOutcome.Absent); }
            if (reader.GetInt32(1) <= 0) { return new(OperationalMetricCoverageReadOutcome.Corrupt); }
            if (reader.GetInt32(1) != 1) { return new(OperationalMetricCoverageReadOutcome.UnsupportedEncoding); }
            subjectId = reader.GetInt64(0);
            storedIdentity = (byte[])reader[2];
            processor = reader.GetInt64(3);
            stream = reader.GetInt64(4);
            position = reader.GetDecimal(5);
        }

        try
        {
            if (OperationalMetricCoverageV1Codec.DecodeSubject(storedIdentity) != subject
                || !storedIdentity.AsSpan().SequenceEqual(identity)) { return new(OperationalMetricCoverageReadOutcome.Corrupt); }
        }
        catch (Exception exception) when (IsEncodingFailure(exception)) { return new(OperationalMetricCoverageReadOutcome.Corrupt); }

        var binding = await ResolveSourceAsync(connection, transaction, subject, cancellationToken);
        if (binding is null || !binding.Valid || binding.Processor != processor || binding.Stream != stream
            || position != checked((decimal)subject.SourceRevision.Position.Value)) { return new(OperationalMetricCoverageReadOutcome.Corrupt); }

        long? head;
        await using (var command = Command(connection, transaction, "SELECT HeadRevision FROM dbo.OperationalMetricCoverageHead WHERE SubjectId = @Subject;"))
        {
            command.Parameters.Add("@Subject", SqlDbType.BigInt).Value = subjectId;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            head = result is long value ? value : null;
        }

        await using (var command = Command(connection, transaction, """
            SELECT MIN(AssessmentRevision), MAX(AssessmentRevision), COUNT_BIG(*)
            FROM dbo.OperationalMetricCoverageVersion WHERE SubjectId = @Subject;
            """))
        {
            command.Parameters.Add("@Subject", SqlDbType.BigInt).Value = subjectId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (head is null || head <= 0 || reader.IsDBNull(0) || reader.GetInt64(0) != 1
                || reader.GetInt64(1) != head || reader.GetInt64(2) != head)
            {
                return new(OperationalMetricCoverageReadOutcome.Corrupt);
            }
        }

        byte[]? proposed = null;
        OperationalMetricCoverageAssessment? assessment = null;
        await using (var command = Command(connection, transaction, """
            SELECT AssessmentRevision, ContentCodecVersion, ContentBinary
            FROM dbo.OperationalMetricCoverageVersion WHERE SubjectId = @Subject ORDER BY AssessmentRevision;
            """))
        {
            command.Parameters.Add("@Subject", SqlDbType.BigInt).Value = subjectId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(1) <= 0) { return new(OperationalMetricCoverageReadOutcome.Corrupt); }
                if (reader.GetInt32(1) != 1) { return new(OperationalMetricCoverageReadOutcome.UnsupportedEncoding); }
                var bytes = (byte[])reader[2];
                OperationalMetricCoverageAssessment decoded;
                try { decoded = OperationalMetricCoverageV1Codec.DecodeContent(subject, bytes); }
                catch (Exception exception) when (IsEncodingFailure(exception)) { return new(OperationalMetricCoverageReadOutcome.Corrupt); }
                if (reader.GetInt64(0) == revision) { proposed = bytes; assessment = decoded; }
            }
        }

        return new(proposed is null ? OperationalMetricCoverageReadOutcome.Absent : OperationalMetricCoverageReadOutcome.Found,
            subjectId, head, proposed, assessment);
    }

    private static bool IsEncodingFailure(Exception exception) => exception is ArgumentException or InvalidDataException or EndOfStreamException or OverflowException;
    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string text)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    private static void AddBytes(SqlCommand command, string name, byte[] value, int size = -1) =>
        command.Parameters.Add(name, SqlDbType.VarBinary, size).Value = value;
    private static void AddVersionKey(SqlCommand command, long subject, long revision)
    {
        command.Parameters.Add("@Subject", SqlDbType.BigInt).Value = subject;
        command.Parameters.Add("@Revision", SqlDbType.BigInt).Value = revision;
    }

    private sealed class CoverageLockException : Exception;
    private sealed record SourceBinding(long Processor, long Stream, bool Valid);
    private sealed record StoredState(OperationalMetricCoverageReadOutcome Outcome, long? SubjectId = null,
        long? Head = null, byte[]? ProposedContent = null, OperationalMetricCoverageAssessment? Assessment = null);
}
