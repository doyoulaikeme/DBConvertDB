using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace DbConvert
{
    public class VerifyOutcome
    {
        public long SourceCount { get; set; } = -1;
        public long TargetCount { get; set; } = -1;
        public string SourceSum { get; set; } = "";
        public string TargetSum { get; set; } = "";
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>行数 + 全表内容校验和（与库无关的归一化哈希），不一致时尝试定位第一处差异。</summary>
    public class Verifier
    {
        private readonly SchemaSession _src;
        private readonly SchemaSession _dst;
        private readonly OptionsConfig _opt;
        private readonly Action<string> _log;
        private readonly bool _emptyAsNull;

        public Verifier(SchemaSession src, SchemaSession dst, OptionsConfig opt, Action<string> log)
        {
            _src = src;
            _dst = dst;
            _opt = opt;
            _log = log ?? (s => { });
            _emptyAsNull = (opt != null && opt.EmptyStringAsNull) ||
                           (dst != null && dst.Dialect.Family == DialectFamily.Oracle);
        }

        public VerifyOutcome Verify(TableDef t, string where)
        {
            var columns = t.DataColumns();

            var outcome = new VerifyOutcome();
            string srcRef = _src.Dialect.Qualify(t.Schema, t.Name);
            string dstRef = _dst.Dialect.Qualify(t.TargetSchema, t.TargetName);
            string whereClause = string.IsNullOrWhiteSpace(where) ? "" : " WHERE (" + where + ")";

            outcome.SourceCount = CountRows(_src, srcRef + whereClause);
            outcome.TargetCount = CountRows(_dst, dstRef + whereClause);
            if (outcome.SourceCount != outcome.TargetCount)
            {
                outcome.Ok = false;
                outcome.Message = "行数不一致：源 " + outcome.SourceCount + "，目标 " + outcome.TargetCount;
                return outcome;
            }

            if (string.Equals(_opt.VerifyMode, "count", StringComparison.OrdinalIgnoreCase) || columns.Count == 0)
            {
                outcome.Ok = true;
                outcome.Message = "行数一致（" + outcome.SourceCount + " 行）";
                return outcome;
            }

            string orderBy = BuildOrderBy(t);
            outcome.SourceSum = ComputeSum(_src, srcRef, columns, whereClause, orderBy);
            outcome.TargetSum = ComputeSum(_dst, dstRef, columns, whereClause, orderBy);

            if (string.Equals(outcome.SourceSum, outcome.TargetSum, StringComparison.Ordinal))
            {
                outcome.Ok = true;
                outcome.Message = "行数与内容校验和一致（" + outcome.SourceCount + " 行）";
                return outcome;
            }

            outcome.Ok = false;
            outcome.Message = "内容校验和不一致（源 " + outcome.SourceSum + " / 目标 " + outcome.TargetSum + "）";
            string diff = FindFirstDifference(t, columns, whereClause);
            if (!string.IsNullOrEmpty(diff)) outcome.Message += "；" + diff;
            return outcome;
        }

        private long CountRows(SchemaSession s, string fromWhere)
        {
            using (var cmd = s.Connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM " + fromWhere;
                if (_opt.CommandTimeout > 0) cmd.CommandTimeout = _opt.CommandTimeout;
                object v = cmd.ExecuteScalar();
                return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
        }

        private string BuildOrderBy(TableDef t)
        {
            IndexDef pk = t.PrimaryKey;
            if (pk == null || pk.Columns.Count == 0) return "";
            var parts = new List<string>();
            foreach (IndexColumn ic in pk.Columns) parts.Add(_src.Dialect.Quote(ic.Name));
            return " ORDER BY " + string.Join(", ", parts);
        }

        private string ComputeSum(SchemaSession s, string tableRef, List<ColumnDef> columns, string whereClause, string orderBy)
        {
            var cols = new List<string>();
            foreach (ColumnDef c in columns)
            {
                cols.Add(s.Dialect.Quote(_src == s ? c.Name : c.TargetName));
            }
            string sql = "SELECT " + string.Join(", ", cols) + " FROM " + tableRef + whereClause + orderBy;

            ulong sum = 0;
            using (var cmd = s.Connection.CreateCommand())
            {
                cmd.CommandText = sql;
                if (_opt.CommandTimeout > 0) cmd.CommandTimeout = _opt.CommandTimeout;
                using (var rd = cmd.ExecuteReader(CommandBehavior.SequentialAccess))
                {
                    object[] values = new object[columns.Count];
                    while (rd.Read())
                    {
                        for (int i = 0; i < columns.Count; i++)
                        {
                            values[i] = DataPump.Unwrap(rd.GetValue(i));
                        }
                        sum = unchecked(sum + HashRow(values, columns, _emptyAsNull));
                    }
                }
            }
            return sum.ToString("X16", CultureInfo.InvariantCulture);
        }

        public static ulong HashRow(object[] values, List<ColumnDef> columns, bool emptyAsNull = false)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            for (int i = 0; i < columns.Count; i++)
            {
                string s = ValueConverter.Normalize(values[i], columns[i], emptyAsNull);
                byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
                foreach (byte b in bytes)
                {
                    h ^= b;
                    h *= prime;
                }
                h ^= 0x02;
                h *= prime;
            }
            return h;
        }

        /// <summary>逐行比对，找到第一处差异（最多扫描 20 万行）。</summary>
        private string FindFirstDifference(TableDef t, List<ColumnDef> columns, string whereClause)
        {
            IndexDef pk = t.PrimaryKey;
            if (pk == null || pk.Columns.Count == 0) return "无主键，无法逐行定位差异";
            string orderBy = BuildOrderBy(t);
            string srcRef = _src.Dialect.Qualify(t.Schema, t.Name);
            string dstRef = _dst.Dialect.Qualify(t.TargetSchema, t.TargetName);

            var srcCols = new List<string>();
            var dstCols = new List<string>();
            foreach (ColumnDef c in columns)
            {
                srcCols.Add(_src.Dialect.Quote(c.Name));
                dstCols.Add(_dst.Dialect.Quote(c.TargetName));
            }
            string srcSql = "SELECT " + string.Join(", ", srcCols) + " FROM " + srcRef + whereClause + orderBy;
            string dstSql = "SELECT " + string.Join(", ", dstCols) + " FROM " + dstRef + whereClause + orderBy;

            var srcCmd = _src.Connection.CreateCommand();
            srcCmd.CommandText = srcSql;
            if (_opt.CommandTimeout > 0) srcCmd.CommandTimeout = _opt.CommandTimeout;
            var dstCmd = _dst.Connection.CreateCommand();
            dstCmd.CommandText = dstSql;
            if (_opt.CommandTimeout > 0) dstCmd.CommandTimeout = _opt.CommandTimeout;

            try
            {
                using (var r1 = srcCmd.ExecuteReader())
                using (var r2 = dstCmd.ExecuteReader())
                {
                    long row = 0;
                    object[] v1 = new object[columns.Count];
                    object[] v2 = new object[columns.Count];
                    while (r1.Read() && r2.Read())
                    {
                        row++;
                        if (row > 200000) return "前 20 万行未发现差异，请按主键区间分批校验";
                        r1.GetValues(v1);
                        r2.GetValues(v2);
                        for (int i = 0; i < columns.Count; i++)
                        {
                            string n1 = ValueConverter.Normalize(DataPump.Unwrap(v1[i]), columns[i], _emptyAsNull);
                            string n2 = ValueConverter.Normalize(DataPump.Unwrap(v2[i]), columns[i], _emptyAsNull);
                            if (n1 != n2)
                            {
                                var sb = new StringBuilder();
                                sb.Append("第 ").Append(row).Append(" 行不一致，主键 ");
                                foreach (IndexColumn ic in pk.Columns)
                                {
                                    ColumnDef pc = t.FindColumn(ic.Name);
                                    int idx = columns.IndexOf(pc);
                                    sb.Append(ic.Name).Append('=').Append(idx >= 0 ? ValueConverter.ToStringValue(v1[idx]) : "?").Append(' ');
                                }
                                sb.Append("，列 ").Append(columns[i].Name).Append("：源 ").Append(Trunc(n1))
                                  .Append(" / 目标 ").Append(Trunc(n2));
                                return sb.ToString();
                            }
                        }
                    }
                    if (r1.Read() || r2.Read()) return "行数或排序不一致，两侧行集不同";
                }
            }
            finally
            {
                srcCmd.Dispose();
                dstCmd.Dispose();
            }
            return "";
        }

        private static string Trunc(string s)
        {
            if (s == null) return "NULL";
            return s.Length <= 80 ? "'" + s + "'" : "'" + s.Substring(0, 80) + "...'";
        }
    }
}
