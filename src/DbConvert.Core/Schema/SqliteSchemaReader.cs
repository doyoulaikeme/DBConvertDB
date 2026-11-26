using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace DbConvert.Schema
{
    /// <summary>SQLite 结构读取（PRAGMA）。</summary>
    public class SqliteSchemaReader : ISchemaReader
    {
        public string ResolveSchema(DbConnection conn, string configured)
        {
            return string.IsNullOrWhiteSpace(configured) ? "main" : configured;
        }

        public List<TableDef> ReadTables(DbConnection conn, string schema, Action<string> log)
        {
            var tables = new List<TableDef>();
            var ddl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using (var rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        string name = Convert.ToString(rd[0]);
                        string sql = rd[1] == DBNull.Value ? "" : Convert.ToString(rd[1]);
                        tables.Add(new TableDef { Schema = "main", Name = name });
                        ddl[name] = sql ?? "";
                    }
                }
            }

            foreach (var t in tables)
            {
                ReadColumns(conn, t, ddl[t.Name]);
                ReadChecks(t, ddl[t.Name]);
                ReadIndexes(conn, t);
                ReadForeignKeys(conn, t);
                t.RowCountEstimate = SchemaSql.CountRows(conn, "\"" + t.Name.Replace("\"", "\"\"") + "\"");
            }
            return tables;
        }

        /// <summary>SQLite 的 CHECK 只存在于 CREATE TABLE 原文里，按括号配对抽出来。</summary>
        private void ReadChecks(TableDef table, string createSql)
        {
            if (string.IsNullOrWhiteSpace(createSql)) return;
            int pos = 0;
            int n = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                int idx = IndexOfKeyword(createSql, "CHECK", pos);
                if (idx < 0) break;
                int open = createSql.IndexOf('(', idx + 5);
                if (open < 0) break;
                int close = FindMatching(createSql, open);
                if (close < 0) break;
                string expr = createSql.Substring(open + 1, close - open - 1).Trim();
                if (expr.Length > 0 && seen.Add(expr))
                {
                    n++;
                    table.Checks.Add(new CheckDef { Name = "ck_" + table.Name + "_" + n, Expression = expr });
                }
                pos = close + 1;
            }
        }

        private void ReadForeignKeys(DbConnection conn, TableDef table)
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA foreign_key_list(\"" + table.Name.Replace("\"", "\"\"") + "\")";
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string id = acc.Str("id") ?? "";
                            ForeignKeyDef fk = null;
                            foreach (var e in table.ForeignKeys)
                            {
                                if (string.Equals(e.Name, "FK_" + table.Name + "_" + id, StringComparison.OrdinalIgnoreCase)) { fk = e; break; }
                            }
                            if (fk == null)
                            {
                                fk = new ForeignKeyDef
                                {
                                    Name = "FK_" + table.Name + "_" + id,
                                    RefTable = acc.Str("table") ?? "",
                                    OnUpdate = acc.Str("on_update") ?? "",
                                    OnDelete = acc.Str("on_delete") ?? ""
                                };
                                table.ForeignKeys.Add(fk);
                            }
                            string from = acc.Str("from");
                            if (!string.IsNullOrEmpty(from)) fk.Columns.Add(from);
                            string to = acc.Str("to");
                            if (!string.IsNullOrEmpty(to)) fk.RefColumns.Add(to);
                        }
                    }
                }
            }
            catch
            {
                // 外键读取失败不影响主流程
            }
        }

        private void ReadColumns(DbConnection conn, TableDef table, string createSql)
        {
            string quoted = "\"" + table.Name.Replace("\"", "\"\"") + "\"";
            using (var cmd = conn.CreateCommand())
            {
                // table_info 会隐藏生成列（hidden=2/3），必须用 table_xinfo 才能拿到
                cmd.CommandText = "PRAGMA table_xinfo(" + quoted + ")";
                using (var rd = cmd.ExecuteReader())
                {
                    var acc = new RowAccess(rd);
                    while (rd.Read())
                    {
                        int hidden = acc.Int("hidden") ?? 0;
                        if (hidden == 1) continue; // 虚拟表的隐藏列
                        var col = new ColumnDef
                        {
                            Name = acc.Str("name") ?? "",
                            Ordinal = (acc.Int("cid") ?? 0) + 1,
                            Nullable = !acc.Bool("notnull")
                        };
                        col.TargetName = col.Name;
                        MapType(col, acc.Str("type") ?? "");
                        if (hidden == 2 || hidden == 3)
                        {
                            string expr = ExtractGeneratedExpression(createSql, col.Name);
                            if (string.IsNullOrWhiteSpace(expr))
                            {
                                // 拿不到表达式就不要当计算列，否则 DDL 会建成普通列、搬数时又被排除，值会丢
                                col.MappingWarning = "生成列没解析出表达式，已按普通列处理并搬数据，目标库需要手工补 AS (...)";
                            }
                            else
                            {
                                col.IsComputed = true;
                                col.GeneratedStored = hidden == 3;
                                col.GenerationExpression = expr;
                            }
                        }
                        string dflt = acc.Str("dflt_value");
                        if (!string.IsNullOrEmpty(dflt) && !col.IsComputed)
                        {
                            col.HasDefault = true;
                            col.DefaultExpression = dflt;
                        }
                        table.Columns.Add(col);
                    }
                }
            }

            bool autoIncrement = createSql.IndexOf("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase) >= 0;
            // INTEGER PRIMARY KEY（单列）本身就是 rowid 别名，插入时自动生成
            var pkCols = new List<ColumnDef>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(" + quoted + ")";
                using (var rd = cmd.ExecuteReader())
                {
                    var acc = new RowAccess(rd);
                    while (rd.Read())
                    {
                        int pk = acc.Int("pk") ?? 0;
                        string name = acc.Str("name");
                        if (pk > 0)
                        {
                            ColumnDef c = table.FindColumn(name);
                            if (c != null && pkCols.IndexOf(c) < 0) pkCols.Add(c);
                        }
                    }
                }
            }

            if (pkCols.Count > 0)
            {
                var pk = new IndexDef { Name = "pk_" + table.Name, IsPrimary = true, IsUnique = true };
                foreach (var c in pkCols) pk.Columns.Add(new IndexColumn { Name = c.Name });
                table.Indexes.Add(pk);
            }

            if (pkCols.Count == 1 && pkCols[0].Kind == ColumnKind.Int)
            {
                pkCols[0].IsIdentity = true;
                if (autoIncrement) pkCols[0].MappingWarning = "源库使用了 AUTOINCREMENT";
            }
        }

        /// <summary>从 CREATE TABLE 原文里解析某列的生成表达式（SQLite 的 PRAGMA 不返回表达式）。</summary>
        private static string ExtractGeneratedExpression(string createSql, string columnName)
        {
            if (string.IsNullOrWhiteSpace(createSql)) return null;
            int open = createSql.IndexOf('(');
            if (open < 0) return null;
            int close = FindMatching(createSql, open);
            string body = createSql.Substring(open + 1, (close < 0 ? createSql.Length : close) - open - 1);

            foreach (string segment in SplitTopLevel(body))
            {
                string def = segment.Trim();
                if (def.Length == 0) continue;
                string head = ReadIdentifier(def, out int afterHead);
                if (!string.Equals(head, columnName, StringComparison.OrdinalIgnoreCase)) continue;

                int asIdx = IndexOfKeyword(def, "AS", afterHead);
                if (asIdx < 0) continue;
                int exprOpen = def.IndexOf('(', asIdx);
                if (exprOpen < 0) continue;
                int exprClose = FindMatching(def, exprOpen);
                if (exprClose < 0) continue;
                string expr = def.Substring(exprOpen + 1, exprClose - exprOpen - 1).Trim();
                return expr.Length == 0 ? null : expr;
            }
            return null;
        }

        private static string ReadIdentifier(string s, out int end)
        {
            end = 0;
            if (s.Length == 0) return "";
            char c = s[0];
            if (c == '"' || c == '`' || c == '\'' || c == '[')
            {
                char closeCh = c == '[' ? ']' : c;
                var sb = new System.Text.StringBuilder();
                int i = 1;
                while (i < s.Length)
                {
                    if (s[i] == closeCh)
                    {
                        if (closeCh != ']' && i + 1 < s.Length && s[i + 1] == closeCh) { sb.Append(closeCh); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(s[i]);
                    i++;
                }
                end = i;
                return sb.ToString();
            }
            int j = 0;
            while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] != '(') j++;
            end = j;
            return s.Substring(0, j);
        }

        private static int IndexOfKeyword(string s, string keyword, int start)
        {
            for (int i = Math.Max(0, start); i + keyword.Length <= s.Length; i++)
            {
                if (string.Compare(s, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                bool leftOk = i == 0 || !char.IsLetterOrDigit(s[i - 1]);
                bool rightOk = i + keyword.Length >= s.Length || !char.IsLetterOrDigit(s[i + keyword.Length]);
                if (leftOk && rightOk) return i;
            }
            return -1;
        }

        private static int FindMatching(string s, int openIndex)
        {
            int depth = 0;
            for (int i = openIndex; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' || c == '"' || c == '`')
                {
                    char q = c;
                    i++;
                    while (i < s.Length && s[i] != q) i++;
                    continue;
                }
                if (c == '[')
                {
                    while (i < s.Length && s[i] != ']') i++;
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static List<string> SplitTopLevel(string s)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' || c == '"' || c == '`')
                {
                    char q = c;
                    i++;
                    while (i < s.Length && s[i] != q) i++;
                    continue;
                }
                if (c == '[')
                {
                    while (i < s.Length && s[i] != ']') i++;
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        private void MapType(ColumnDef col, string declared)
        {
            string raw = (declared ?? "").Trim();
            string upper = raw.ToUpperInvariant();
            col.RawType = raw;
            col.SourceTypeName = raw;

            string baseType = upper;
            int? length = null;
            int? precision = null;
            int? scale = null;
            int open = upper.IndexOf('(');
            if (open > 0)
            {
                int close = upper.IndexOf(')', open);
                if (close > open)
                {
                    string inner = upper.Substring(open + 1, close - open - 1);
                    string[] parts = inner.Split(',');
                    if (parts.Length >= 1 && parts[0].Trim().Length > 0 && parts[0].Trim().All(char.IsDigit)) length = Convert.ToInt32(parts[0].Trim());
                    if (parts.Length >= 2 && parts[1].Trim().All(char.IsDigit)) scale = Convert.ToInt32(parts[1].Trim());
                    baseType = (upper.Substring(0, open) + upper.Substring(close + 1)).Trim();
                }
            }

            if (baseType.Contains("INT"))
            {
                if (baseType.Contains("BIG")) col.Kind = ColumnKind.BigInt;
                else if (baseType.Contains("TINY")) col.Kind = ColumnKind.TinyInt;
                else if (baseType.Contains("SMALL")) col.Kind = ColumnKind.SmallInt;
                else col.Kind = ColumnKind.Int;
                return;
            }
            if (baseType.Contains("CHAR") && baseType.Contains("VAR"))
            {
                col.Kind = ColumnKind.VarChar;
                col.Length = length;
                return;
            }
            if (baseType.StartsWith("NCHAR") || baseType.StartsWith("NVARCHAR"))
            {
                col.Kind = ColumnKind.NVarChar;
                col.Length = length;
                return;
            }
            if (baseType.Contains("CHAR") || baseType.Contains("CLOB") || baseType.Contains("TEXT"))
            {
                if (length.HasValue && length.Value > 0 && baseType.StartsWith("CHAR"))
                {
                    col.Kind = ColumnKind.Char;
                    col.Length = length;
                }
                else if (length.HasValue && length.Value > 0 && baseType.StartsWith("VARCHAR"))
                {
                    col.Kind = ColumnKind.VarChar;
                    col.Length = length;
                }
                else col.Kind = ColumnKind.Text;
                return;
            }
            if (baseType.Contains("BLOB") || baseType == "IMAGE")
            {
                col.Kind = ColumnKind.Blob;
                return;
            }
            if (baseType.Contains("BINARY"))
            {
                col.Kind = ColumnKind.Binary;
                col.Length = length;
                return;
            }
            if (baseType.Contains("REAL") || baseType.Contains("FLOA") || baseType.Contains("DOUB"))
            {
                col.Kind = ColumnKind.Double;
                return;
            }
            if (baseType.Contains("DEC") || baseType.Contains("NUM"))
            {
                col.Kind = ColumnKind.Decimal;
                col.Precision = precision ?? length;
                col.Scale = scale;
                if (!col.Precision.HasValue)
                {
                    col.Precision = 38;
                    col.Scale = 10;
                }
                return;
            }
            if (baseType.Contains("BOOL"))
            {
                col.Kind = ColumnKind.Boolean;
                return;
            }
            if (baseType.Contains("DATETIME") || baseType.Contains("TIMESTAMP"))
            {
                col.Kind = ColumnKind.DateTime;
                col.DateTimePrecision = null;
                return;
            }
            if (baseType == "DATE")
            {
                col.Kind = ColumnKind.Date;
                return;
            }
            if (baseType == "TIME")
            {
                col.Kind = ColumnKind.Time;
                return;
            }
            if (baseType.Contains("JSON"))
            {
                col.Kind = ColumnKind.Json;
                return;
            }
            if (baseType.Contains("UUID") || baseType.Contains("GUID"))
            {
                col.Kind = ColumnKind.Guid;
                return;
            }
            // 无类型声明（SQLite 允许）按 TEXT 处理
            col.Kind = string.IsNullOrEmpty(baseType) ? ColumnKind.Text : ColumnKind.Unknown;
            if (col.Kind == ColumnKind.Unknown) col.MappingWarning = "未识别的 SQLite 声明类型 " + raw;
        }

        private void ReadIndexes(DbConnection conn, TableDef table)
        {
            string quoted = "\"" + table.Name.Replace("\"", "\"\"") + "\"";
            var indexNames = new List<string>();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA index_list(" + quoted + ")";
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string name = acc.Str("name");
                            if (string.IsNullOrEmpty(name)) continue;
                            bool unique = acc.Bool("unique");
                            string origin = acc.Str("origin") ?? "";
                            if (origin.Equals("pk", StringComparison.OrdinalIgnoreCase)) continue; // 主键单独处理
                            var ix = new IndexDef { Name = name, IsUnique = unique };
                            using (var c2 = conn.CreateCommand())
                            {
                                c2.CommandText = "PRAGMA index_info(\"" + name.Replace("\"", "\"\"") + "\")";
                                using (var r2 = c2.ExecuteReader())
                                {
                                    var acc2 = new RowAccess(r2);
                                    while (r2.Read())
                                    {
                                        string cname = acc2.Str("name");
                                        if (!string.IsNullOrEmpty(cname)) ix.Columns.Add(new IndexColumn { Name = cname });
                                    }
                                }
                            }
                            if (ix.Columns.Count > 0) table.Indexes.Add(ix);
                        }
                    }
                }
            }
            catch
            {
                // 索引读取失败不影响主流程
            }
        }

        public List<ViewDef> ReadViews(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<ViewDef>();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'view' ORDER BY name";
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            string name = Convert.ToString(rd[0]);
                            string sql = rd[1] == DBNull.Value ? "" : Convert.ToString(rd[1]);
                            list.Add(new ViewDef
                            {
                                Schema = "main",
                                Name = name,
                                Definition = ExtractViewSelect(sql)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 SQLite 视图失败：" + ex.Message);
            }
            return list;
        }

        /// <summary>从 CREATE VIEW ... AS 后面截出 SELECT 主体。</summary>
        private static string ExtractViewSelect(string createViewSql)
        {
            string sql = (createViewSql ?? "").Trim();
            int idx = IndexOfWord(sql, "AS");
            if (idx < 0) return sql;
            return sql.Substring(idx + 2).Trim();
        }

        private static int IndexOfWord(string s, string word)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s, "\\b" + word + "\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Index : -1;
        }

        public List<RoutineDef> ReadRoutines(DbConnection conn, string schema, Action<string> log)
        {
            // SQLite 没有存储过程
            return new List<RoutineDef>();
        }

        public List<TriggerDef> ReadTriggers(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<TriggerDef>();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name, tbl_name, sql FROM sqlite_master WHERE type = 'trigger' ORDER BY name";
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.Read())
                        {
                            string sql = rd[2] == DBNull.Value ? "" : Convert.ToString(rd[2]);
                            list.Add(new TriggerDef
                            {
                                Schema = "main",
                                Name = Convert.ToString(rd[0]),
                                Table = Convert.ToString(rd[1]),
                                Timing = sql.IndexOf("BEFORE", StringComparison.OrdinalIgnoreCase) >= 0 ? "BEFORE" :
                                         (sql.IndexOf("INSTEAD OF", StringComparison.OrdinalIgnoreCase) >= 0 ? "INSTEAD OF" : "AFTER"),
                                Definition = sql
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 SQLite 触发器失败：" + ex.Message);
            }
            return list;
        }

        public long CountRows(DbConnection conn, string schema, string table)
        {
            return SchemaSql.CountRows(conn, "\"" + table.Replace("\"", "\"\"") + "\"");
        }
    }
}
