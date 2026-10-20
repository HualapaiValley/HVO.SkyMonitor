using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

public sealed partial class SqliteSwpcStore
{
    internal const string Schema = """
        CREATE TABLE swpc_meta(id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL CHECK(revision>=0), ingest_sequence INTEGER NOT NULL CHECK(ingest_sequence>=0), provider_halt TEXT CHECK(provider_halt IS NULL OR json_valid(provider_halt))) STRICT;
        CREATE TABLE swpc_raw(sha TEXT PRIMARY KEY CHECK(length(sha)=64 AND sha NOT GLOB '*[^0-9A-F]*'), body BLOB NOT NULL) STRICT;
        CREATE TABLE swpc_snapshots(id INTEGER PRIMARY KEY AUTOINCREMENT, product INTEGER NOT NULL CHECK(product BETWEEN 0 AND 3), sha TEXT NOT NULL CHECK(length(sha)=64 AND sha NOT GLOB '*[^0-9A-F]*'), retrieved INTEGER NOT NULL, envelope TEXT NOT NULL CHECK(json_valid(envelope)), raw_sha TEXT NOT NULL REFERENCES swpc_raw(sha)) STRICT;
        CREATE INDEX swpc_snapshots_age ON swpc_snapshots(retrieved,id);
        CREATE TABLE swpc_products(product INTEGER PRIMARY KEY CHECK(product BETWEEN 0 AND 3), lkg INTEGER REFERENCES swpc_snapshots(id), etag TEXT, last_modified TEXT, last_success INTEGER, last_attempt INTEGER, last_outcome INTEGER CHECK(last_outcome IS NULL OR last_outcome BETWEEN 0 AND 7), checkpoint TEXT NOT NULL CHECK(json_valid(checkpoint)), halt TEXT CHECK(halt IS NULL OR json_valid(halt))) STRICT;
        CREATE TABLE swpc_messages(identity TEXT NOT NULL, hash TEXT NOT NULL CHECK(length(hash)=64 AND hash NOT GLOB '*[^0-9A-F]*'), sequence INTEGER NOT NULL UNIQUE CHECK(sequence>0), record TEXT NOT NULL CHECK(json_valid(record)), original BLOB NOT NULL, snapshot_sha TEXT NOT NULL CHECK(length(snapshot_sha)=64 AND snapshot_sha NOT GLOB '*[^0-9A-F]*'), retrieved INTEGER NOT NULL, PRIMARY KEY(identity,hash)) STRICT;
        CREATE TABLE swpc_ranks(code TEXT NOT NULL, serial INTEGER NOT NULL CHECK(serial BETWEEN 0 AND 999999), rank INTEGER NOT NULL CHECK(rank BETWEEN 0 AND 5), PRIMARY KEY(code,serial)) STRICT;
        CREATE TABLE swpc_acks(code TEXT NOT NULL, serial INTEGER NOT NULL CHECK(serial BETWEEN 0 AND 999999), actor TEXT NOT NULL CHECK(length(actor) BETWEEN 1 AND 128), at_utc INTEGER NOT NULL, PRIMARY KEY(code,serial)) STRICT;
        CREATE TABLE swpc_orphans(identity TEXT PRIMARY KEY) STRICT;
        CREATE TABLE swpc_attempts(sequence INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, product INTEGER NOT NULL REFERENCES swpc_products(product), started INTEGER NOT NULL, start_fingerprint TEXT NOT NULL CHECK(length(start_fingerprint)=64), start TEXT NOT NULL CHECK(json_valid(start)), completion TEXT CHECK(completion IS NULL OR json_valid(completion)), snapshot_sha TEXT CHECK(snapshot_sha IS NULL OR (length(snapshot_sha)=64 AND snapshot_sha NOT GLOB '*[^0-9A-F]*')), completion_fingerprint TEXT CHECK(completion_fingerprint IS NULL OR length(completion_fingerprint)=64), added INTEGER NOT NULL DEFAULT 0 CHECK(added>=0), diagnostics TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(diagnostics)), CHECK((completion IS NULL AND completion_fingerprint IS NULL AND snapshot_sha IS NULL AND added=0) OR (completion IS NOT NULL AND completion_fingerprint IS NOT NULL))) STRICT;
        CREATE UNIQUE INDEX swpc_attempts_single_flight ON swpc_attempts((1)) WHERE completion IS NULL;
        CREATE INDEX swpc_attempts_retention ON swpc_attempts(started,sequence) WHERE completion IS NOT NULL;
        CREATE TABLE swpc_receipts(kind TEXT NOT NULL CHECK(kind IN ('halt','ack')), id TEXT NOT NULL, created INTEGER NOT NULL, fingerprint TEXT NOT NULL CHECK(length(fingerprint)=64), added INTEGER NOT NULL CHECK(added>=0), diagnostics TEXT NOT NULL CHECK(json_valid(diagnostics)), PRIMARY KEY(kind,id)) STRICT;
        CREATE INDEX swpc_receipts_retention ON swpc_receipts(created);
        """;
    private static void VerifySchema(SqliteConnection db)
    {
        using var tx = db.BeginTransaction(deferred: false);
        var version = Convert.ToInt64(Scalar(db, tx, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version == 0)
        {
            if (Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%';"), CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("Space-weather storage contains an unversioned schema.");
            Exec(db, tx, Schema);
            Exec(db, tx, "INSERT INTO swpc_meta(id,revision,ingest_sequence) VALUES(1,0,0);");
            foreach (var p in Enum.GetValues<SwpcProduct>()) Exec(db, tx, "INSERT INTO swpc_products(product,checkpoint) VALUES($p,$c);", ("$p", (int)p), ("$c", Json(EmptyCheckpoint)));
            Exec(db, tx, "PRAGMA user_version=1;");
        }
        else if (version != SchemaVersion) throw new InvalidDataException("Unsupported space-weather schema version; upgrade forward with a compatible binary.");
        using var canonical = new SqliteConnection("Data Source=:memory:"); canonical.Open(); Exec(canonical, null, Schema);
        var expected = Definitions(canonical, null); var actual = Definitions(db, tx);
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var sql) || sql != pair.Value))
            throw new InvalidDataException("The space-weather schema differs from its canonical version.");
        if (!Equals(Scalar(db, tx, "PRAGMA integrity_check;"), "ok") || Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM pragma_foreign_key_check;"), CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Space-weather SQLite integrity validation failed.");
        if (Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM swpc_products;"), CultureInfo.InvariantCulture) != 4 || Convert.ToInt64(Scalar(db, tx, "SELECT count(*) FROM swpc_meta;"), CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("Space-weather durable state rows are missing.");
        tx.Commit();
    }
    private static Dictionary<string, string> Definitions(SqliteConnection db, SqliteTransaction? tx)
    {
        using var command = Command(db, tx, "SELECT name,type,tbl_name,sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY name;");
        using var reader = command.ExecuteReader(); var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1) + ":" + reader.GetString(2) + ":" + SqliteRawCaptureJournal.NormalizeSchemaSql(reader.GetString(3)));
        return result;
    }
}
