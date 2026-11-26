using System;
using System.Collections.Generic;
using System.Text;

namespace DbConvert
{
    public enum DialectFamily
    {
        MySql,
        Postgres,
        Oracle,
        SqlServer,
        Sqlite
    }

    /// <summary>目标库方言：标识符引用、参数前缀、类型映射、DDL 语法差异。</summary>
    public class Dialect
    {
        public string Key = "";
        public string Display = "";
        public DialectFamily Family = DialectFamily.Postgres;
        public string QuoteOpen = "\"";
        public string QuoteClose = "\"";
        public string ParameterPrefix = "@";
        public bool SupportsMultiRowInsert = true;
        public int MaxParameters = 2000;
        public int MaxVarcharChars = 4000;
        public int MaxDecimalPrecision = 38;
        public bool SupportsCommentOn = true;
        public bool SupportsIfNotExists = true;
        public bool SupportsTruncate = true;
        public string DefaultSchema = "";

        public string Quote(string identifier)
        {
            if (string.IsNullOrEmpty(identifier)) return identifier;
            return QuoteOpen + identifier.Replace(QuoteClose, QuoteClose + QuoteClose) + QuoteClose;
        }

        public string Qualify(string schema, string name)
        {
            if (string.IsNullOrEmpty(schema)) return Quote(name);
            return Quote(schema) + "." + Quote(name);
        }

        private string T(string s) => s;

        /// <summary>把与库无关的列语义映射为目标库类型；返回 null 表示无法映射。</summary>
        public string MapType(ColumnDef col, OptionsConfig opt)
        {
            int chars = col.Length.HasValue && col.Length.Value > 0 ? col.Length.Value : 0;
            bool unicode = col.Kind == ColumnKind.NChar || col.Kind == ColumnKind.NVarChar ||
                           (Family == DialectFamily.SqlServer && opt.SqlServerUnicode);

            switch (col.Kind)
            {
                case ColumnKind.Boolean:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "TINYINT(1)";
                        case DialectFamily.Postgres: return "BOOLEAN";
                        case DialectFamily.Oracle: return "NUMBER(1)";
                        case DialectFamily.SqlServer: return "BIT";
                        default: return "INTEGER";
                    }

                case ColumnKind.TinyInt:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return col.IsUnsigned ? "TINYINT UNSIGNED" : "TINYINT";
                        case DialectFamily.Postgres: return "SMALLINT";
                        case DialectFamily.Oracle: return "NUMBER(3)";
                        case DialectFamily.SqlServer: return "TINYINT";
                        default: return "INTEGER";
                    }

                case ColumnKind.SmallInt:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return col.IsUnsigned ? "SMALLINT UNSIGNED" : "SMALLINT";
                        case DialectFamily.Postgres: return "SMALLINT";
                        case DialectFamily.Oracle: return col.IsUnsigned ? "NUMBER(5)" : "NUMBER(5)";
                        case DialectFamily.SqlServer: return "SMALLINT";
                        default: return "INTEGER";
                    }

                case ColumnKind.Int:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return col.IsUnsigned ? "INT UNSIGNED" : "INT";
                        case DialectFamily.Postgres: return "INTEGER";
                        case DialectFamily.Oracle: return col.IsUnsigned ? "NUMBER(10)" : "NUMBER(10)";
                        case DialectFamily.SqlServer: return "INT";
                        default: return "INTEGER";
                    }

                case ColumnKind.BigInt:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return col.IsUnsigned ? "BIGINT UNSIGNED" : "BIGINT";
                        case DialectFamily.Postgres: return col.IsUnsigned ? "NUMERIC(20)" : "BIGINT";
                        case DialectFamily.Oracle: return col.IsUnsigned ? "NUMBER(20)" : "NUMBER(19)";
                        case DialectFamily.SqlServer: return "BIGINT";
                        default: return "INTEGER";
                    }

                case ColumnKind.Decimal:
                {
                    int p = col.Precision ?? 18;
                    int s = col.Scale ?? 0;
                    if (p <= 0) p = 18;
                    if (s < 0) s = 0;
                    if (p > MaxDecimalPrecision)
                    {
                        p = MaxDecimalPrecision;
                        col.MappingWarning = "源精度 " + col.Precision + " 超过目标上限 " + MaxDecimalPrecision + "，已收敛";
                    }
                    if (s > p)
                    {
                        s = p;
                        col.MappingWarning = "scale 大于 precision，已收敛为 " + p + "," + p;
                    }
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "DECIMAL(" + p + "," + s + ")";
                        case DialectFamily.Postgres: return "NUMERIC(" + p + "," + s + ")";
                        case DialectFamily.Oracle: return "NUMBER(" + p + "," + s + ")";
                        case DialectFamily.SqlServer: return "DECIMAL(" + p + "," + s + ")";
                        case DialectFamily.Sqlite: return "NUMERIC(" + p + "," + s + ")";
                        default: return "NUMERIC";
                    }
                }

                case ColumnKind.Money:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "DECIMAL(19,4)";
                        case DialectFamily.Postgres: return "NUMERIC(19,4)";
                        case DialectFamily.Oracle: return "NUMBER(19,4)";
                        case DialectFamily.SqlServer: return "MONEY";
                        default: return "NUMERIC";
                    }

                case ColumnKind.Float:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "FLOAT";
                        case DialectFamily.Postgres: return "REAL";
                        case DialectFamily.Oracle: return Key == "dm" ? "FLOAT" : "BINARY_FLOAT";
                        case DialectFamily.SqlServer: return "REAL";
                        default: return "REAL";
                    }

                case ColumnKind.Double:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "DOUBLE";
                        case DialectFamily.Postgres: return "DOUBLE PRECISION";
                        case DialectFamily.Oracle: return Key == "dm" ? "DOUBLE" : "BINARY_DOUBLE";
                        case DialectFamily.SqlServer: return "FLOAT(53)";
                        default: return "REAL";
                    }

                case ColumnKind.Char:
                case ColumnKind.NChar:
                {
                    int len = chars > 0 ? chars : 1;
                    if (MaxVarcharChars > 0 && len > MaxVarcharChars) return TextType(unicode);
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "CHAR(" + len + ")";
                        case DialectFamily.Postgres: return "CHAR(" + len + ")";
                        case DialectFamily.Oracle: return unicode ? "NCHAR(" + len + ")" : "CHAR(" + (chars > 0 && opt.VarcharLengthMode == "bytes" ? len * opt.BytesPerChar : len) + ")";
                        case DialectFamily.SqlServer: return (SizeForSqlServer(len, unicode) == null ? "NVARCHAR(MAX)" : (unicode ? "NCHAR(" : "CHAR(") + SizeForSqlServer(len, unicode) + ")");
                        case DialectFamily.Sqlite: return "CHAR(" + len + ")";
                        default: return "TEXT";
                    }
                }

                case ColumnKind.VarChar:
                case ColumnKind.NVarChar:
                {
                    int len = chars > 0 ? chars : 255;
                    if (MaxVarcharChars > 0 && len > MaxVarcharChars)
                    {
                        if (chars > MaxVarcharChars) col.MappingWarning = "源长度 " + chars + " 超过目标 VARCHAR 上限，已改为大文本";
                        return TextType(unicode);
                    }
                    switch (Family)
                    {
                        case DialectFamily.MySql:
                            return (unicode || true) ? "VARCHAR(" + len + ")" : "VARCHAR(" + len + ")";
                        case DialectFamily.Postgres: return "VARCHAR(" + len + ")";
                        case DialectFamily.Oracle:
                            if (unicode) return "NVARCHAR2(" + len + ")";
                            return "VARCHAR2(" + (opt.VarcharLengthMode == "bytes" ? len * opt.BytesPerChar : len) + ")";
                        case DialectFamily.SqlServer:
                        {
                            int? size = SizeForSqlServer(len, unicode);
                            if (size == null) return "NVARCHAR(MAX)";
                            return (unicode ? "NVARCHAR(" : "VARCHAR(") + size + ")";
                        }
                        case DialectFamily.Sqlite: return "VARCHAR(" + len + ")";
                        default: return "TEXT";
                    }
                }

                case ColumnKind.Text:
                    return TextType(unicode);

                case ColumnKind.Binary:
                case ColumnKind.Blob:
                {
                    switch (Family)
                    {
                        case DialectFamily.MySql:
                            if (col.Kind == ColumnKind.Binary && chars > 0 && chars <= 255) return "VARBINARY(" + chars + ")";
                            return "LONGBLOB";
                        case DialectFamily.Postgres: return "BYTEA";
                        case DialectFamily.Oracle: return "BLOB";
                        case DialectFamily.SqlServer: return "VARBINARY(MAX)";
                        default: return "BLOB";
                    }
                }

                case ColumnKind.Date:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "DATE";
                        case DialectFamily.Postgres: return "DATE";
                        case DialectFamily.Oracle: return "DATE";
                        case DialectFamily.SqlServer: return "DATE";
                        case DialectFamily.Sqlite: return "DATE";
                        default: return "TEXT";
                    }

                case ColumnKind.Time:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "TIME(6)";
                        case DialectFamily.Postgres: return "TIME(6)";
                        case DialectFamily.Oracle: return "VARCHAR(15)";
                        case DialectFamily.SqlServer: return "TIME(7)";
                        case DialectFamily.Sqlite: return "TIME";
                        default: return "TEXT";
                    }

                case ColumnKind.DateTime:
                {
                    int p = col.DateTimePrecision.HasValue ? Math.Min(col.DateTimePrecision.Value, 6) : 6;
                    switch (Family)
                    {
                        case DialectFamily.MySql: return p == 0 ? "DATETIME" : "DATETIME(" + p + ")";
                        case DialectFamily.Postgres: return p == 0 ? "TIMESTAMP" : "TIMESTAMP(" + p + ")";
                        case DialectFamily.Oracle:
                            if (opt.DateTimePolicy == "date" || (opt.DateTimePolicy == "auto" && p == 0)) return "DATE";
                            return "TIMESTAMP(" + Math.Max(p, 1) + ")";
                        case DialectFamily.SqlServer: return p == 0 ? "DATETIME2" : "DATETIME2(" + p + ")";
                        case DialectFamily.Sqlite: return "DATETIME";
                        default: return "TEXT";
                    }
                }

                case ColumnKind.TimestampTz:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "DATETIME(6)";
                        case DialectFamily.Postgres: return "TIMESTAMP(6) WITH TIME ZONE";
                        case DialectFamily.Oracle: return Key == "dm" ? "TIMESTAMP(6)" : "TIMESTAMP(6) WITH TIME ZONE";
                        case DialectFamily.SqlServer: return "DATETIMEOFFSET(6)";
                        case DialectFamily.Sqlite: return "DATETIME";
                        default: return "TEXT";
                    }

                case ColumnKind.Guid:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "CHAR(36)";
                        case DialectFamily.Postgres: return "UUID";
                        case DialectFamily.Oracle: return "VARCHAR2(36)";
                        case DialectFamily.SqlServer: return "UNIQUEIDENTIFIER";
                        case DialectFamily.Sqlite: return "CHAR(36)";
                        default: return "TEXT";
                    }

                case ColumnKind.Json:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "JSON";
                        case DialectFamily.Postgres: return "JSONB";
                        case DialectFamily.Oracle: return "CLOB";
                        case DialectFamily.SqlServer: return "NVARCHAR(MAX)";
                        default: return "TEXT";
                    }

                case ColumnKind.Xml:
                    switch (Family)
                    {
                        case DialectFamily.MySql: return "LONGTEXT";
                        case DialectFamily.Postgres: return "XML";
                        case DialectFamily.Oracle: return "CLOB";
                        case DialectFamily.SqlServer: return "XML";
                        default: return "TEXT";
                    }

                case ColumnKind.Interval:
                    switch (Family)
                    {
                        case DialectFamily.Postgres: return "INTERVAL";
                        case DialectFamily.Oracle: return "INTERVAL DAY TO SECOND";
                        default: return "VARCHAR(64)";
                    }

                case ColumnKind.Enum:
                    return "VARCHAR(64)";

                default:
                    return null;
            }
        }

        private int? SizeForSqlServer(int chars, bool unicode)
        {
            int max = unicode ? 4000 : 8000;
            if (chars <= 0) return 1;
            if (chars > max) return null;
            return chars;
        }

        public string TextType(bool unicode)
        {
            switch (Family)
            {
                case DialectFamily.MySql: return "LONGTEXT";
                case DialectFamily.Postgres: return "TEXT";
                case DialectFamily.Oracle: return "CLOB";
                case DialectFamily.SqlServer: return "NVARCHAR(MAX)";
                default: return "TEXT";
            }
        }

        public string IntegerType(bool big)
        {
            switch (Family)
            {
                case DialectFamily.MySql: return big ? "BIGINT" : "INT";
                case DialectFamily.Postgres: return big ? "BIGINT" : "INTEGER";
                case DialectFamily.Oracle: return big ? "NUMBER(19)" : "NUMBER(10)";
                case DialectFamily.SqlServer: return big ? "BIGINT" : "INT";
                default: return "INTEGER";
            }
        }

        /// <summary>自增列的内联子句；sequence 模式由生成器额外处理。</summary>
        public string IdentityClause(ColumnDef col, OptionsConfig opt)
        {
            if (col.IsIdentity == false) return "";
            string mode = opt.IdentityMode ?? "auto";
            if (mode == "none") return "";
            if (mode == "sequence") return "";

            switch (Family)
            {
                case DialectFamily.MySql: return " AUTO_INCREMENT";
                case DialectFamily.Postgres: return " GENERATED BY DEFAULT AS IDENTITY";
                case DialectFamily.Oracle:
                    return Key == "dm" ? " IDENTITY(1,1)" : " GENERATED BY DEFAULT AS IDENTITY";
                case DialectFamily.SqlServer: return " IDENTITY(1,1)";
                default: return "";
            }
        }

        public string TruncateStatement(string schema, string table)
        {
            string q = Qualify(schema, table);
            if (Family == DialectFamily.Sqlite) return "DELETE FROM " + q + ";";
            return "TRUNCATE TABLE " + q + ";";
        }
    }

    public static class Dialects
    {
        private static readonly Dictionary<string, string> AliasToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "mysql", "mysql" },
            { "mariadb", "mysql" },
            { "tidb", "mysql" },
            { "oceanbase", "mysql" },
            { "oceanbase-mysql", "mysql" },
            { "gbase8a", "mysql" },
            { "gbase-8a", "mysql" },
            { "polardb-mysql", "mysql" },
            { "postgres", "postgres" },
            { "postgresql", "postgres" },
            { "pg", "postgres" },
            { "opengauss", "postgres" },
            { "open-gauss", "postgres" },
            { "gaussdb", "postgres" },
            { "kingbase", "postgres" },
            { "kingbasees", "postgres" },
            { "highgo", "postgres" },
            { "polardb-pg", "postgres" },
            { "oracle", "oracle" },
            { "dm", "dm" },
            { "dameng", "dm" },
            { "dm8", "dm" },
            { "sqlserver", "sqlserver" },
            { "mssql", "sqlserver" },
            { "sqlite", "sqlite" },
            { "sqlite3", "sqlite" }
        };

        private static readonly Dictionary<string, Dialect> Cache = new Dictionary<string, Dialect>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<string> Kinds
        {
            get
            {
                yield return "mysql";
                yield return "tidb";
                yield return "oceanbase";
                yield return "gbase8a";
                yield return "postgres";
                yield return "opengauss";
                yield return "kingbase";
                yield return "highgo";
                yield return "oracle";
                yield return "dm";
                yield return "sqlserver";
                yield return "sqlite";
            }
        }

        public static string NormalizeKind(string kind)
        {
            if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("kind 不能为空");
            string k = kind.Trim().ToLowerInvariant();
            if (!AliasToKey.ContainsKey(k))
            {
                throw new ArgumentException("不支持的数据库类型 '" + kind + "'，可用：" + string.Join(", ", Kinds));
            }
            return AliasToKey[k];
        }

        public static Dialect Get(string kind)
        {
            string key = NormalizeKind(kind);
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out Dialect d)) return d;
                d = Create(key, kind);
                Cache[key] = d;
                return d;
            }
        }

        private static Dialect Create(string key, string rawKind)
        {
            var d = new Dialect();
            d.Key = key;
            string k = (rawKind ?? key).Trim().ToLowerInvariant();

            switch (key)
            {
                case "mysql":
                    d.Display = k == "mysql" ? "MySQL" : k.ToUpperInvariant();
                    d.Family = DialectFamily.MySql;
                    d.QuoteOpen = "`";
                    d.QuoteClose = "`";
                    d.ParameterPrefix = "@";
                    d.SupportsMultiRowInsert = true;
                    d.MaxParameters = 60000;
                    d.MaxVarcharChars = 16383;
                    d.MaxDecimalPrecision = 65;
                    d.SupportsCommentOn = false;
                    d.SupportsIfNotExists = true;
                    d.DefaultSchema = "";
                    break;

                case "postgres":
                    d.Display = k == "postgres" ? "PostgreSQL" : k.ToUpperInvariant();
                    d.Family = DialectFamily.Postgres;
                    d.QuoteOpen = "\"";
                    d.QuoteClose = "\"";
                    d.ParameterPrefix = "@";
                    d.SupportsMultiRowInsert = true;
                    d.MaxParameters = 60000;
                    d.MaxVarcharChars = 10485760;
                    d.MaxDecimalPrecision = 1000;
                    d.SupportsCommentOn = true;
                    d.SupportsIfNotExists = true;
                    d.DefaultSchema = "public";
                    break;

                case "oracle":
                    d.Display = "Oracle";
                    d.Family = DialectFamily.Oracle;
                    d.MaxParameters = 3000;
                    d.MaxVarcharChars = 4000;
                    d.MaxDecimalPrecision = 38;
                    d.SupportsCommentOn = true;
                    d.SupportsIfNotExists = false;
                    d.DefaultSchema = "";
                    break;

                case "dm":
                    d.Display = "达梦 DM";
                    d.Family = DialectFamily.Oracle;
                    d.MaxParameters = 3000;
                    d.MaxVarcharChars = 8000;
                    d.MaxDecimalPrecision = 38;
                    d.SupportsCommentOn = true;
                    d.SupportsIfNotExists = false;
                    d.DefaultSchema = "";
                    break;

                case "sqlserver":
                    d.Display = "SQL Server";
                    d.Family = DialectFamily.SqlServer;
                    d.QuoteOpen = "[";
                    d.QuoteClose = "]";
                    d.MaxParameters = 2000;
                    d.MaxVarcharChars = 8000;
                    d.MaxDecimalPrecision = 38;
                    d.SupportsCommentOn = false;
                    d.SupportsIfNotExists = false;
                    d.DefaultSchema = "dbo";
                    break;

                case "sqlite":
                    d.Display = "SQLite";
                    d.Family = DialectFamily.Sqlite;
                    d.QuoteOpen = "\"";
                    d.QuoteClose = "\"";
                    d.MaxParameters = 800;
                    d.MaxVarcharChars = 0;
                    d.MaxDecimalPrecision = 38;
                    d.SupportsCommentOn = false;
                    d.SupportsIfNotExists = true;
                    d.DefaultSchema = "main";
                    break;

                default:
                    throw new ArgumentException("未知方言 " + key);
            }
            return d;
        }
    }

    public static class SqlUtil
    {
        public static string ApplyCase(string name, string identifierCase)
        {
            if (string.IsNullOrEmpty(name)) return name;
            switch ((identifierCase ?? "preserve").ToLowerInvariant())
            {
                case "upper": return name.ToUpperInvariant();
                case "lower": return name.ToLowerInvariant();
                default: return name;
            }
        }

        /// <summary>auto：跟着目标库的默认折叠规则走（Oracle/达梦大写、PG 小写、其余保留）。</summary>
        public static string ResolveCase(string configured, Dialect d)
        {
            string c = (configured ?? "auto").Trim().ToLowerInvariant();
            if (c == "upper" || c == "lower" || c == "preserve") return c;
            if (d == null) return "preserve";
            if (d.Family == DialectFamily.Oracle) return "upper";
            if (d.Family == DialectFamily.Postgres) return "lower";
            return "preserve";
        }

        public static string EscapeString(string s)
        {
            if (s == null) return "";
            return s.Replace("'", "''");
        }

        public static string Literal(string s)
        {
            return "'" + EscapeString(s) + "'";
        }

        /// <summary>拆分 "schema.table"（无库名时 schema 为空）。</summary>
        public static void SplitQualified(string qualified, out string schema, out string table)
        {
            qualified = (qualified ?? "").Trim();
            int idx = qualified.IndexOf('.');
            if (idx < 0)
            {
                schema = "";
                table = qualified;
                return;
            }
            schema = qualified.Substring(0, idx).Trim();
            table = qualified.Substring(idx + 1).Trim();
        }

        public static string Join(string schema, string table)
        {
            return string.IsNullOrEmpty(schema) ? table : schema + "." + table;
        }

        public static string Format(object value)
        {
            if (value == null || value is DBNull) return "NULL";
            if (value is DateTime) return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fffffff");
            if (value is byte[]) return "<binary>";
            if (value is bool) return ((bool)value) ? "1" : "0";
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
        }
    }
}

