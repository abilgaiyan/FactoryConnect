using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

// Default invocation and --self-test never open a SQL connection.
internal static class Program
{
    const string SqlHash="dbb7af00aa97241f789d982b43a5740c774a79973360bcb6ba66d51d8cac7719";
    const string Server=@"DESKTOP-1PJK2OB\FCINVENTORY";
    const string Database="FactoryConnect_GG003_IMP1C_INVENTORY_20261020";
    static readonly string[] Names={"Q2B-target","Q2B-cut","Q2B-contribution-boundary","Q2B-fact-membership","Q2B-duration-operands","Q2B-coverage","Q2B-end-target"};
    static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower};
    static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    static void Require(bool value,string message) { if(!value) throw new InvalidDataException(message); }
    static object? ConvertValue(string type,object value)
    {
        if(value is DBNull || value is INullable n && n.IsNull) return null;
        return type switch
        {
            "decimal" or "numeric" when value is SqlDecimal d => d.ToString(),
            "datetimeoffset" when value is DateTimeOffset d => d.ToString("O",CultureInfo.InvariantCulture),
            "date" when value is DateTime d => d.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
            "nvarchar" or "varchar" or "nchar" or "char" when value is SqlString s => s.Value,
            "bigint" when value is SqlInt64 i => i.Value.ToString(CultureInfo.InvariantCulture),
            "int" when value is SqlInt32 i => i.Value.ToString(CultureInfo.InvariantCulture),
            "smallint" when value is SqlInt16 i => i.Value.ToString(CultureInfo.InvariantCulture),
            "tinyint" when value is SqlByte i => i.Value.ToString(CultureInfo.InvariantCulture),
            "bit" when value is SqlBoolean b => b.Value,
            "binary" or "varbinary" when value is SqlBinary b => Convert.ToHexString(b.Value),
            "uniqueidentifier" when value is SqlGuid g => g.Value.ToString("D"),
            "sql_variant" when value is string s => new {provider_type="System.String",value=s},
            _=>throw new InvalidDataException("Unsupported provider value/type: "+type)
        };
    }
    static object? Cell(SqlDataReader reader,int i,string type)
    {
        if(reader.IsDBNull(i)) return null;
        // SqlDecimal preserves SQL precision up to 38; never use CLR decimal/double.
        object value=type switch {
            "datetimeoffset"=>reader.GetFieldValue<DateTimeOffset>(i),
            "date"=>reader.GetDateTime(i),
            "sql_variant"=>reader.GetValue(i),
            _=>reader.GetSqlValue(i)};
        return ConvertValue(type,value);
    }
    static void SelfTest()
    {
        Require((string)ConvertValue("decimal",SqlDecimal.Parse("12345678901234567890.123456789012345678"))! =="12345678901234567890.123456789012345678","38 digit decimal");
        Require((string)ConvertValue("decimal",SqlDecimal.Parse("18446744073709551615"))! =="18446744073709551615","UInt64 decimal");
        var stamp=DateTimeOffset.ParseExact("2026-10-16T06:40:52.0133557+05:30","O",CultureInfo.InvariantCulture);
        Require((string)ConvertValue("datetimeoffset",stamp)! =="2026-10-16T06:40:52.0133557+05:30","7 digit timestamp");
        Require(ConvertValue("nvarchar",SqlString.Null) is null,"NULL");
        Require((string)ConvertValue("nvarchar",new SqlString(" \r\nहिन्दी 😀 |"))! ==" \r\nहिन्दी 😀 |","Unicode");
        Require((string)ConvertValue("bigint",new SqlInt64(long.MaxValue))! =="9223372036854775807","bigint");
        bool rejected=false; try {ConvertValue("decimal",0.1d);} catch(InvalidDataException){rejected=true;}
        Require(rejected,"float rejection");
        Console.WriteLine("provider-value-self-test: PASS (7 checks; NO database)");
    }
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture=CultureInfo.InvariantCulture;
        if(args.SequenceEqual(new[]{"--self-test"})){SelfTest();return 0;}
        if(args.Length!=4 || args[0]!="--execute-authorized"){
            Console.Error.WriteLine("Database execution disabled by default. Offline: --self-test. Live requires separately reviewed authorization envelope.");
            return 2;
        }
        // args: --execute-authorized envelope.json frozen.sql spool.jsonl
        string? spool=null; bool success=false, created=false;
        try
        {
            Require(OperatingSystem.IsWindows(),"Windows integrated restricted-reader path only");
            using var envelope=JsonDocument.Parse(await File.ReadAllBytesAsync(args[1]));
            var e=envelope.RootElement;
            Require(e.GetProperty("authorized").GetBoolean(),"Execution not authorized");
            Require(e.GetProperty("server").GetString()==Server && e.GetProperty("database").GetString()==Database,"Envelope target");
            Require(e.GetProperty("sql_sha256").GetString()==SqlHash,"Envelope SQL identity");
            Require(!string.IsNullOrWhiteSpace(e.GetProperty("authorization_id").GetString()),"Authorization ID");
            Require(!string.IsNullOrWhiteSpace(e.GetProperty("expected_original_login").GetString()),"Approved login");
            Require(e.GetProperty("timeout_seconds").GetInt32()==60 && e.GetProperty("max_spool_bytes").GetInt64()==10485760,"Frozen limits");
            var sql=await File.ReadAllBytesAsync(args[2]);
            Require(Hash(sql)==SqlHash,"Frozen SQL bytes");
            string text=new UTF8Encoding(false,true).GetString(sql);
            spool=Path.GetFullPath(args[3]);
            Require(!File.Exists(spool) && !File.Exists(spool+".success.json"),"Spool/receipt exists");
            // Exclusive CreateNew spool is explicitly incomplete until terminal success.
            await using var file=new FileStream(spool,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            created=true;
            using var prefix=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes=0;
            async Task Emit(object value,bool hash=true)
            {
                var line=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,JsonOptions)+"\n");
                bytes=checked(bytes+line.Length);Require(bytes<=10485760,"Spool byte limit");
                await file.WriteAsync(line);
                if(hash)prefix.AppendData(line);
            }
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct=deadline.Token;
            var builder=new SqlConnectionStringBuilder { DataSource=Server,InitialCatalog=Database,
                IntegratedSecurity=true,Pooling=false,ConnectTimeout=15,ApplicationName="FactoryConnect-Q2B-reviewed-capture",
                ApplicationIntent=ApplicationIntent.ReadOnly,TrustServerCertificate=true};
            await using var connection=new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command=connection.CreateCommand();
            command.CommandText=text;command.CommandTimeout=60;
            using var cancellation=ct.Register(()=>{try{command.Cancel();}catch{}});
            var counts=new List<int>();
            var reader=await command.ExecuteReaderAsync(ct);
            try
            {
                do
                {
                    int ordinal=counts.Count;Require(ordinal<7 && reader.FieldCount>0,"Extra/unexpected result set");
                    var cols=reader.GetColumnSchema();
                    Require(cols.All(c=>c.AllowDBNull.HasValue && c.ColumnOrdinal.HasValue && c.DataTypeName is not null),"Indeterminate provider metadata");
                    var metadata=cols.Select(c=> {
                        var d=new Dictionary<string,object?> {
                            ["ordinal"]=c.ColumnOrdinal,["name"]=c.ColumnName,
                            ["sql_type"]=c.DataTypeName!.ToLowerInvariant(),["nullable"]=c.AllowDBNull!.Value,
                            ["provider_size"]=c.ColumnSize,["provider_precision"]=c.NumericPrecision,["provider_scale"]=c.NumericScale};
                        if(d["sql_type"]!.ToString() is "decimal" or "numeric"){
                            Require(c.NumericPrecision.HasValue && c.NumericScale.HasValue,"Decimal metadata");
                            d["precision"]=c.NumericPrecision;d["scale"]=c.NumericScale;}
                        if(d["sql_type"]!.ToString()=="datetimeoffset"){Require(c.NumericScale.HasValue,"Timestamp scale");d["scale"]=c.NumericScale;}
                        return d;
                    }).ToArray();
                    await Emit(new {kind="schema",ordinal,columns=metadata});
                    int rows=0;
                    while(await reader.ReadAsync(ct))
                    {
                        Require(rows<1000,"Result row limit");
                        Require(!reader.IsDBNull(0) && reader.GetString(0)==Names[ordinal],"Result label/order");
                        var values=Enumerable.Range(0,reader.FieldCount).Select(i=>Cell(reader,i,cols[i].DataTypeName!.ToLowerInvariant())).ToArray();
                        await Emit(new {kind="row",ordinal,row_ordinal=rows,values});rows++;
                    }
                    counts.Add(rows);
                } while(await reader.NextResultAsync(ct));
                Require(counts.Count==7,"Missing result set");
            }
            finally { await reader.DisposeAsync().AsTask().WaitAsync(ct); }
            // NextResult(false) AND disposal AND connection closure must succeed
            // before any terminal success marker can be emitted.
            await connection.CloseAsync().WaitAsync(ct);
            await command.DisposeAsync().AsTask().WaitAsync(ct);
            await connection.DisposeAsync().AsTask().WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            await Emit(new {kind="terminal",command_success=true,reader_disposed=true,connection_closed=true,
                sql_sha256=SqlHash,prefix_sha256=Convert.ToHexStringLower(prefix.GetHashAndReset()),
                row_counts=counts,authorization_id=e.GetProperty("authorization_id").GetString(),
                provider=typeof(SqlConnection).Assembly.FullName},false);
            await file.FlushAsync(ct);file.Flush(true);
            await file.DisposeAsync();
            // Sidecar exists only after command/connection/file finalization.
            var complete=await File.ReadAllBytesAsync(spool,ct);
            byte[] receipt=JsonSerializer.SerializeToUtf8Bytes(new {capture_success=true,
                spool_sha256=Hash(complete),sql_sha256=SqlHash,
                authorization_id=e.GetProperty("authorization_id").GetString()},JsonOptions);
            await using(var done=new FileStream(spool+".success.json",FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                await done.WriteAsync(receipt,ct);await done.FlushAsync(ct);done.Flush(true);
            }
            success=true;return 0;
        }
        catch(Exception ex){Console.Error.WriteLine("Capture failed: "+ex.GetType().Name);return 1;}
        finally {if(!success && created && spool is not null){try{File.Delete(spool);File.Delete(spool+".success.json");}catch{ /* incomplete file has no valid terminal marker */ }}}
    }
}
