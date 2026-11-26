using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DbConvert.Schema
{
    public interface ISchemaReader
    {
        /// <summary>返回当前连接的默认 schema / owner / database。</summary>
        string ResolveSchema(DbConnection conn, string configured);

        /// <summary>读取指定 schema 下的基础表结构（列、主键、索引、行数估算）。</summary>
        List<TableDef> ReadTables(DbConnection conn, string schema, Action<string> log);

        /// <summary>视图定义（不含表结构）。</summary>
        List<ViewDef> ReadViews(DbConnection conn, string schema, Action<string> log);

        /// <summary>存储过程 / 函数源码。</summary>
        List<RoutineDef> ReadRoutines(DbConnection conn, string schema, Action<string> log);

        /// <summary>触发器源码。</summary>
        List<TriggerDef> ReadTriggers(DbConnection conn, string schema, Action<string> log);

        long CountRows(DbConnection conn, string schema, string table);
    }

    public static class SchemaReaderFactory
    {
        public static ISchemaReader Create(string kind)
        {
            string key = Dialects.NormalizeKind(kind);
            switch (key)
            {
                case "mysql": return new MySqlSchemaReader();
                case "postgres": return new PostgresSchemaReader();
                case "oracle": return new OracleSchemaReader();
                case "dm": return new OracleSchemaReader();
                case "sqlserver": return new SqlServerSchemaReader();
                case "sqlite": return new SqliteSchemaReader();
                default: throw new ArgumentException("没有 " + kind + " 的结构读取器");
            }
        }
    }

    /// <summary>按名字安全读取 DataReader 列，兼容不同版本数据字典缺少字段。</summary>
    public class RowAccess
    {
        private readonly DbDataReader _reader;
        private readonly Dictionary<string, int> _map;

        public RowAccess(DbDataReader reader)
        {
            _reader = reader;
            _map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                _map[reader.GetName(i)] = i;
            }
        }

        public bool Has(string name) => _map.ContainsKey(name);

        public object Raw(string name)
        {
            if (!_map.ContainsKey(name)) return null;
            try
            {
                object v = _reader.GetValue(_map[name]);
                return v is DBNull ? null : v;
            }
            catch
            {
                return null;
            }
        }

        public string Str(string name)
        {
            object v = Raw(name);
            if (v == null) return null;
            try { return Convert.ToString(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        public int? Int(string name)
        {
            object v = Raw(name);
            if (v == null) return null;
            try
            {
                string s = Convert.ToString(v, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(s)) return null;
                return Convert.ToInt32(decimal.Parse(s, CultureInfo.InvariantCulture));
            }
            catch { return null; }
        }

        public long? Long(string name)
        {
            object v = Raw(name);
            if (v == null) return null;
            try
            {
                string s = Convert.ToString(v, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(s)) return null;
                return Convert.ToInt64(decimal.Parse(s, CultureInfo.InvariantCulture));
            }
            catch { return null; }
        }

        public bool Bool(string name)
        {
            string s = Str(name);
            if (s == null) return false;
            s = s.Trim().ToLowerInvariant();
            return s == "1" || s == "yes" || s == "y" || s == "true" || s == "t";
        }
    }

    public static class SchemaSql
    {
        public static int ExecuteNonQuery(DbConnection conn, string sql)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.CommandTimeout = 60;
                return cmd.ExecuteNonQuery();
            }
        }

        public static object Scalar(DbConnection conn, string sql)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.CommandTimeout = 60;
                object v = cmd.ExecuteScalar();
                return v is DBNull ? null : v;
            }
        }

        public static long CountRows(DbConnection conn, string quotedTable)
        {
            object v = Scalar(conn, "SELECT COUNT(*) FROM " + quotedTable);
            if (v == null) return -1;
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        /// <summary>LIKE 模式的表名过滤器，支持 *、?，逗号分隔多个模式。</summary>
        public static bool Match(string qualifiedName, string schema, string table, string include, string exclude)
        {
            string q = qualifiedName ?? table;
            if (!MatchAny(q, table, include, true)) return false;
            if (!string.IsNullOrWhiteSpace(exclude) && MatchAny(q, table, exclude, false)) return false;
            return true;
        }

        private static bool MatchAny(string qualified, string table, string patterns, bool emptyResult)
        {
            if (string.IsNullOrWhiteSpace(patterns)) return emptyResult;
            string[] items = patterns.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string itemRaw in items)
            {
                string item = itemRaw.Trim();
                if (item.Length == 0) continue;
                if (item == "*") return true;
                Regex rx = GlobToRegex(item);
                if (rx.IsMatch(qualified) || rx.IsMatch(table)) return true;
            }
            return !emptyResult;
        }

        private static readonly Dictionary<string, Regex> RegexCache = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);

        private static Regex GlobToRegex(string pattern)
        {
            lock (RegexCache)
            {
                if (RegexCache.TryGetValue(pattern, out Regex cached)) return cached;
                var sb = new StringBuilder("^");
                foreach (char ch in pattern)
                {
                    switch (ch)
                    {
                        case '*': sb.Append(".*"); break;
                        case '?': sb.Append('.'); break;
                        case '.': sb.Append("\\."); break;
                        default: sb.Append(Regex.Escape(ch.ToString())); break;
                    }
                }
                sb.Append("$");
                var rx = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                RegexCache[pattern] = rx;
                return rx;
            }
        }

        public static string ParseNumericPrecision(string rawType)
        {
            if (string.IsNullOrEmpty(rawType)) return "";
            int i = rawType.IndexOf('(');
            if (i < 0) return "";
            int j = rawType.IndexOf(')', i);
            if (j < 0) return "";
            return rawType.Substring(i + 1, j - i - 1).Trim();
        }
    }
}
