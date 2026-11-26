using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace DbConvert.Schema
{
    /// <summary>Oracle 与达梦 DM8 共用（达梦的数据字典与 Oracle 高度兼容）。</summary>
    public class OracleSchemaReader : ISchemaReader
    {
        public string ResolveSchema(DbConnection conn, string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured.ToUpperInvariant();
            object v = SchemaSql.Scalar(conn, "SELECT USER FROM DUAL");
            return v == null ? "" : Convert.ToString(v).ToUpperInvariant();
        }

        public List<TableDef> ReadTables(DbConnection conn, string schema, Action<string> log)
        {
            if (string.IsNullOrEmpty(schema)) throw new InvalidOperationException("Oracle/达梦需要指定 owner/schema");
            string lit = SqlUtil.Literal(schema);
            var tables = new List<TableDef>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT TABLE_NAME, NUM_ROWS FROM ALL_TABLES WHERE OWNER = " + lit + " ORDER BY TABLE_NAME";
                cmd.CommandTimeout = 180;
                using (var rd = cmd.ExecuteReader())
                {
                    var acc = new RowAccess(rd);
                    while (rd.Read())
                    {
                        var t = new TableDef { Schema = schema, Name = acc.Str("TABLE_NAME") };
                        long? n = acc.Long("NUM_ROWS");
                        t.RowCountEstimate = n.HasValue ? n.Value : -1;
                        tables.Add(t);
                    }
                }
            }

            var byName = new Dictionary<string, TableDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables) byName[t.Name] = t;

            // 表注释
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT TABLE_NAME, COMMENTS FROM ALL_TAB_COMMENTS WHERE OWNER = " + lit +
                                      " AND TABLE_TYPE = 'TABLE'";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("TABLE_NAME");
                            if (tn != null && byName.ContainsKey(tn)) byName[tn].Comment = acc.Str("COMMENTS") ?? "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取表注释失败（忽略）：" + ex.Message);
            }

            ReadColumns(conn, schema, byName, log);
            ReadVirtualColumns(conn, schema, byName, log);
            ReadPrimaryKeys(conn, schema, byName, log);
            ReadIndexes(conn, schema, byName, log);
            ReadIdentity(conn, schema, byName, log);
            ReadConstraints(conn, schema, byName, log);
            return tables;
        }

        private void ReadColumns(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            bool withCharLength = true;
            while (true)
            {
                string sql =
                    "SELECT TABLE_NAME, COLUMN_NAME, COLUMN_ID, DATA_TYPE, DATA_LENGTH" +
                    (withCharLength ? ", CHAR_LENGTH" : "") +
                    ", DATA_PRECISION, DATA_SCALE, NULLABLE, DATA_DEFAULT FROM ALL_TAB_COLUMNS WHERE OWNER = " + lit +
                    " ORDER BY TABLE_NAME, COLUMN_ID";
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = sql;
                        cmd.CommandTimeout = 180;
                        using (var rd = cmd.ExecuteReader())
                        {
                            var acc = new RowAccess(rd);
                            while (rd.Read())
                            {
                                string tn = acc.Str("TABLE_NAME");
                                if (tn == null || !byName.ContainsKey(tn)) continue;
                                var col = new ColumnDef
                                {
                                    Name = acc.Str("COLUMN_NAME") ?? "",
                                    Ordinal = acc.Int("COLUMN_ID") ?? 0,
                                    Nullable = !string.Equals(acc.Str("NULLABLE"), "N", StringComparison.OrdinalIgnoreCase)
                                };
                                col.TargetName = col.Name;
                                int? dataLength = acc.Int("DATA_LENGTH");
                                int? charLength = withCharLength ? acc.Int("CHAR_LENGTH") : null;
                                MapType(col, acc.Str("DATA_TYPE") ?? "", dataLength, charLength,
                                        acc.Int("DATA_PRECISION"), acc.Int("DATA_SCALE"));
                                string dv = acc.Str("DATA_DEFAULT");
                                if (!string.IsNullOrWhiteSpace(dv) && !string.Equals(dv.Trim(), "NULL", StringComparison.OrdinalIgnoreCase))
                                {
                                    col.HasDefault = true;
                                    col.DefaultExpression = dv.Trim();
                                }
                                byName[tn].Columns.Add(col);
                            }
                        }
                    }
                    break;
                }
                catch (Exception ex)
                {
                    if (withCharLength)
                    {
                        log?.Invoke("info: ALL_TAB_COLUMNS 无 CHAR_LENGTH（" + ex.Message + "），按字节长度估算字符数");
                        withCharLength = false;
                        continue;
                    }
                    log?.Invoke("读取列信息失败：" + ex.Message);
                    return;
                }
            }
        }

        /// <summary>Oracle 虚拟列在 DATA_DEFAULT 里存的是表达式，不能当默认值；单独标出来。</summary>
        private void ReadVirtualColumns(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT TABLE_NAME, COLUMN_NAME, DATA_DEFAULT FROM ALL_TAB_COLUMNS WHERE OWNER = " + lit +
                                      " AND VIRTUAL_COLUMN = 'YES'";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("TABLE_NAME");
                            string cn = acc.Str("COLUMN_NAME");
                            string expr = acc.Str("DATA_DEFAULT");
                            if (tn == null || cn == null || !byName.ContainsKey(tn)) continue;
                            ColumnDef c = byName[tn].FindColumn(cn);
                            if (c == null) continue;
                            string cleanExpr = (expr ?? "").Trim();
                            if (cleanExpr.Length == 0)
                            {
                                c.MappingWarning = "虚拟列没读到表达式，已按普通列处理并搬数据";
                                continue;
                            }
                            c.IsComputed = true;
                            c.GenerationExpression = cleanExpr;
                            c.HasDefault = false;
                            c.DefaultExpression = "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("info: 没读到虚拟列信息（" + ex.Message + "），生成列会按普通列处理");
            }
        }

        private void MapType(ColumnDef col, string rawDataType, int? dataLength, int? charLength, int? precision, int? scale)
        {
            string upper = (rawDataType ?? "").Trim().ToUpperInvariant();
            col.RawType = upper;
            col.SourceTypeName = upper;

            // 形如 TIMESTAMP(6) WITH TIME ZONE
            string baseType = upper;
            int? inlinePrecision = null;
            int open = baseType.IndexOf('(');
            if (open > 0)
            {
                int close = baseType.IndexOf(')', open);
                if (close > open)
                {
                    string inner = baseType.Substring(open + 1, close - open - 1).Trim();
                    if (inner.All(char.IsDigit) && inner.Length > 0) inlinePrecision = Convert.ToInt32(inner);
                    baseType = (baseType.Substring(0, open) + baseType.Substring(close + 1)).Trim();
                }
            }
            baseType = baseType.Replace("  ", " ");

            if (baseType.StartsWith("TIMESTAMP"))
            {
                if (baseType.Contains("TIME ZONE")) col.Kind = ColumnKind.TimestampTz;
                else col.Kind = ColumnKind.DateTime;
                col.DateTimePrecision = inlinePrecision ?? 6;
                return;
            }

            switch (baseType)
            {
                case "VARCHAR2":
                case "VARCHAR":
                case "VARCHAR2 CHAR":
                    col.Kind = ColumnKind.VarChar;
                    SetCharLength(col, charLength, dataLength);
                    return;
                case "NVARCHAR2":
                case "NVARCHAR":
                    col.Kind = ColumnKind.NVarChar;
                    SetCharLength(col, charLength, dataLength);
                    return;
                case "CHAR":
                case "CHARACTER":
                    col.Kind = ColumnKind.Char;
                    SetCharLength(col, charLength, dataLength);
                    return;
                case "NCHAR":
                    col.Kind = ColumnKind.NChar;
                    SetCharLength(col, charLength, dataLength);
                    return;
                case "CLOB":
                case "NCLOB":
                case "LONG":
                case "TEXT":
                case "MEDIUMTEXT":
                case "LONGTEXT":
                    col.Kind = ColumnKind.Text;
                    return;
                case "BLOB":
                case "LONG RAW":
                case "IMAGE":
                case "VARBINARY":
                    if (baseType == "VARBINARY" && dataLength.HasValue && dataLength.Value > 0 && dataLength.Value <= 8000)
                    {
                        col.Kind = ColumnKind.Binary;
                        col.Length = dataLength;
                    }
                    else col.Kind = ColumnKind.Blob;
                    return;
                case "RAW":
                    col.Kind = ColumnKind.Binary;
                    col.Length = dataLength;
                    return;
                case "NUMBER":
                case "NUMERIC":
                case "DECIMAL":
                {
                    if (precision.HasValue && precision.Value > 0)
                    {
                        int p = precision.Value;
                        int s = scale ?? 0;
                        if (s > 0)
                        {
                            col.Kind = ColumnKind.Decimal;
                            col.Precision = p;
                            col.Scale = s;
                        }
                        else if (p <= 2)
                        {
                            col.Kind = ColumnKind.TinyInt;
                        }
                        else if (p <= 4)
                        {
                            col.Kind = ColumnKind.SmallInt;
                        }
                        else if (p <= 9)
                        {
                            col.Kind = ColumnKind.Int;
                        }
                        else if (p <= 18)
                        {
                            col.Kind = ColumnKind.BigInt;
                        }
                        else
                        {
                            col.Kind = ColumnKind.Decimal;
                            col.Precision = p;
                            col.Scale = 0;
                        }
                    }
                    else
                    {
                        col.Kind = ColumnKind.Decimal;
                        col.Precision = 38;
                        col.Scale = 10;
                        col.MappingWarning = "NUMBER 未声明精度，按 NUMBER(38,10) 迁移";
                    }
                    return;
                }
                case "INT":
                case "INTEGER":
                case "PLS_INTEGER":
                case "MEDIUMINT":
                    col.Kind = ColumnKind.Int;
                    return;
                case "BIGINT":
                    col.Kind = ColumnKind.BigInt;
                    return;
                case "SMALLINT":
                case "SHORT":
                    col.Kind = ColumnKind.SmallInt;
                    return;
                case "TINYINT":
                case "BYTE":
                    col.Kind = ColumnKind.TinyInt;
                    return;
                case "BIT":
                case "BOOLEAN":
                case "BOOL":
                    col.Kind = ColumnKind.Boolean;
                    return;
                case "FLOAT":
                case "REAL":
                    col.Kind = ColumnKind.Float;
                    return;
                case "DOUBLE":
                case "DOUBLE PRECISION":
                case "BINARY_DOUBLE":
                    col.Kind = ColumnKind.Double;
                    return;
                case "BINARY_FLOAT":
                    col.Kind = ColumnKind.Float;
                    return;
                case "DATE":
                    col.Kind = ColumnKind.DateTime;
                    col.DateTimePrecision = null;
                    return;
                case "DATETIME":
                case "DATETIME2":
                    col.Kind = ColumnKind.DateTime;
                    col.DateTimePrecision = 3;
                    return;
                case "TIME":
                case "TIME WITH TIME ZONE":
                    col.Kind = ColumnKind.Time;
                    return;
                case "INTERVAL DAY TO SECOND":
                case "INTERVAL YEAR TO MONTH":
                    col.Kind = ColumnKind.Interval;
                    return;
                case "XMLTYPE":
                case "XML":
                    col.Kind = ColumnKind.Xml;
                    return;
                case "JSON":
                case "JSONB":
                    col.Kind = ColumnKind.Json;
                    return;
                case "ROWID":
                case "UROWID":
                    col.Kind = ColumnKind.Unknown;
                    col.MappingWarning = "ROWID 不做迁移";
                    return;
                default:
                    col.Kind = ColumnKind.Unknown;
                    col.MappingWarning = "未识别的 Oracle/达梦类型 " + rawDataType;
                    return;
            }
        }

        private void SetCharLength(ColumnDef col, int? charLength, int? dataLength)
        {
            if (charLength.HasValue && charLength.Value > 0)
            {
                col.Length = charLength;
                col.LengthIsBytes = false;
            }
            else if (dataLength.HasValue && dataLength.Value > 0)
            {
                // 只有字节长度时按 3 字节/字估算，宁可放大也不截断
                col.Length = Math.Max(1, (dataLength.Value + 2) / 3);
                col.LengthIsBytes = true;
            }
        }

        private void ReadPrimaryKeys(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT c.TABLE_NAME, c.CONSTRAINT_NAME, cc.COLUMN_NAME, cc.POSITION " +
                        "FROM ALL_CONSTRAINTS c JOIN ALL_CONS_COLUMNS cc " +
                        "ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME AND cc.TABLE_NAME = c.TABLE_NAME " +
                        "WHERE c.OWNER = " + lit + " AND c.CONSTRAINT_TYPE = 'P' " +
                        "ORDER BY c.TABLE_NAME, cc.POSITION";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("TABLE_NAME");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            TableDef t = byName[tn];
                            string cn = acc.Str("CONSTRAINT_NAME") ?? (tn + "_PK");
                            IndexDef ix = null;
                            foreach (var e in t.Indexes)
                            {
                                if (e.IsPrimary) { ix = e; break; }
                            }
                            if (ix == null)
                            {
                                ix = new IndexDef { Name = cn, IsPrimary = true, IsUnique = true };
                                t.Indexes.Insert(0, ix);
                            }
                            string c = acc.Str("COLUMN_NAME");
                            if (!string.IsNullOrEmpty(c)) ix.Columns.Add(new IndexColumn { Name = c });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取主键失败：" + ex.Message);
            }
        }

        private void ReadIndexes(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT i.TABLE_NAME, i.INDEX_NAME, i.UNIQUENESS, ic.COLUMN_NAME, ic.COLUMN_POSITION " +
                        "FROM ALL_INDEXES i JOIN ALL_IND_COLUMNS ic " +
                        "ON ic.INDEX_OWNER = i.OWNER AND ic.INDEX_NAME = i.INDEX_NAME AND ic.TABLE_NAME = i.TABLE_NAME " +
                        "WHERE i.TABLE_OWNER = " + lit + " AND i.INDEX_TYPE <> 'LOB' " +
                        "ORDER BY i.TABLE_NAME, i.INDEX_NAME, ic.COLUMN_POSITION";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("TABLE_NAME");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            TableDef t = byName[tn];
                            string name = acc.Str("INDEX_NAME") ?? "";
                            IndexDef ix = null;
                            foreach (var e in t.Indexes)
                            {
                                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) { ix = e; break; }
                            }
                            if (ix == null)
                            {
                                ix = new IndexDef
                                {
                                    Name = name,
                                    IsUnique = string.Equals(acc.Str("UNIQUENESS"), "UNIQUE", StringComparison.OrdinalIgnoreCase)
                                };
                                t.Indexes.Add(ix);
                            }
                            string c = acc.Str("COLUMN_NAME");
                            if (!string.IsNullOrEmpty(c) && !HasColumn(ix, c)) ix.Columns.Add(new IndexColumn { Name = c });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取索引失败（不影响数据搬运）：" + ex.Message);
            }
        }

        private static bool HasColumn(IndexDef ix, string name)
        {
            foreach (var c in ix.Columns) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void ReadIdentity(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT TABLE_NAME, COLUMN_NAME FROM ALL_TAB_IDENTITY_COLS WHERE OWNER = " + lit;
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("TABLE_NAME");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            string cn = acc.Str("COLUMN_NAME");
                            ColumnDef col = byName[tn].FindColumn(cn);
                            if (col != null) col.IsIdentity = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("info: 无法读取自增列信息（Oracle 11g 或达梦低版本），如目标需要自增请在配置里用 identity 指定：" + ex.Message);
            }
        }

        private void ReadConstraints(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT c.TABLE_NAME, c.CONSTRAINT_NAME, cc.COLUMN_NAME, rc.TABLE_NAME AS R_TABLE, " +
                        "rcc.COLUMN_NAME AS R_COLUMN, c.DELETE_RULE, cc.POSITION " +
                        "FROM ALL_CONSTRAINTS c " +
                        "JOIN ALL_CONS_COLUMNS cc ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME " +
                        "JOIN ALL_CONSTRAINTS rc ON rc.OWNER = c.R_OWNER AND rc.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME " +
                        "JOIN ALL_CONS_COLUMNS rcc ON rcc.OWNER = rc.OWNER AND rcc.CONSTRAINT_NAME = rc.CONSTRAINT_NAME AND rcc.POSITION = cc.POSITION " +
                        "WHERE c.OWNER = " + lit + " AND c.CONSTRAINT_TYPE = 'R' " +
                        "ORDER BY c.TABLE_NAME, c.CONSTRAINT_NAME, cc.POSITION";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("TABLE_NAME");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            TableDef t = byName[tn];
                            string name = acc.Str("CONSTRAINT_NAME") ?? "";
                            ForeignKeyDef fk = null;
                            foreach (var e in t.ForeignKeys)
                            {
                                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) { fk = e; break; }
                            }
                            if (fk == null)
                            {
                                fk = new ForeignKeyDef
                                {
                                    Name = name,
                                    RefTable = acc.Str("R_TABLE") ?? "",
                                    OnDelete = acc.Str("DELETE_RULE") ?? ""
                                };
                                t.ForeignKeys.Add(fk);
                            }
                            string c = acc.Str("COLUMN_NAME");
                            if (!string.IsNullOrEmpty(c)) fk.Columns.Add(c);
                            string rc = acc.Str("R_COLUMN");
                            if (!string.IsNullOrEmpty(rc)) fk.RefColumns.Add(rc);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 Oracle/达梦 外键失败（将不迁移外键）：" + ex.Message);
            }

            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT TABLE_NAME, CONSTRAINT_NAME, SEARCH_CONDITION FROM ALL_CONSTRAINTS " +
                        "WHERE OWNER = " + lit + " AND CONSTRAINT_TYPE = 'C' AND CONSTRAINT_NAME NOT LIKE 'SYS_C%'";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("TABLE_NAME");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            string cond = acc.Str("SEARCH_CONDITION") ?? "";
                            if (cond.IndexOf("IS NOT NULL", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            byName[tn].Checks.Add(new CheckDef
                            {
                                Name = acc.Str("CONSTRAINT_NAME") ?? "",
                                Expression = cond
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 Oracle/达梦 CHECK 约束失败：" + ex.Message);
            }
        }

        public List<ViewDef> ReadViews(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<ViewDef>();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT VIEW_NAME, TEXT FROM ALL_VIEWS WHERE OWNER = " + SqlUtil.Literal(schema) + " ORDER BY VIEW_NAME";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            list.Add(new ViewDef
                            {
                                Schema = schema,
                                Name = acc.Str("VIEW_NAME") ?? "",
                                Definition = acc.Str("TEXT") ?? ""
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 Oracle/达梦 视图失败：" + ex.Message);
            }
            return list;
        }

        public List<RoutineDef> ReadRoutines(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<RoutineDef>();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT NAME, TYPE, LINE, TEXT FROM ALL_SOURCE WHERE OWNER = " + SqlUtil.Literal(schema) +
                        " AND TYPE IN ('PROCEDURE','FUNCTION','PACKAGE','PACKAGE BODY') ORDER BY TYPE, NAME, LINE";
                    cmd.CommandTimeout = 300;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        var builders = new Dictionary<string, RoutineDef>(StringComparer.OrdinalIgnoreCase);
                        while (rd.Read())
                        {
                            string name = acc.Str("NAME") ?? "";
                            string type = acc.Str("TYPE") ?? "";
                            string key = type + "|" + name;
                            if (!builders.TryGetValue(key, out RoutineDef def))
                            {
                                def = new RoutineDef
                                {
                                    Schema = schema,
                                    Name = name,
                                    Kind = type.ToLowerInvariant().Replace(" ", "_")
                                };
                                builders[key] = def;
                                list.Add(def);
                            }
                            def.Definition += acc.Str("TEXT") ?? "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 Oracle/达梦 存储过程/函数失败：" + ex.Message);
            }
            return list;
        }

        public List<TriggerDef> ReadTriggers(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<TriggerDef>();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT TRIGGER_NAME, TABLE_NAME, TRIGGERING_EVENT, TRIGGER_TYPE, TRIGGER_BODY FROM ALL_TRIGGERS " +
                        "WHERE OWNER = " + SqlUtil.Literal(schema) + " ORDER BY TABLE_NAME, TRIGGER_NAME";
                    cmd.CommandTimeout = 180;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string type = acc.Str("TRIGGER_TYPE") ?? "";
                            list.Add(new TriggerDef
                            {
                                Schema = schema,
                                Name = acc.Str("TRIGGER_NAME") ?? "",
                                Table = acc.Str("TABLE_NAME") ?? "",
                                Timing = type.IndexOf("BEFORE", StringComparison.OrdinalIgnoreCase) >= 0 ? "BEFORE" : "AFTER",
                                Event = acc.Str("TRIGGERING_EVENT") ?? "",
                                Definition = acc.Str("TRIGGER_BODY") ?? ""
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 Oracle/达梦 触发器失败：" + ex.Message);
            }
            return list;
        }

        public long CountRows(DbConnection conn, string schema, string table)
        {
            string dialectKey = "oracle";
            var d = Dialects.Get(dialectKey);
            return SchemaSql.CountRows(conn, d.Qualify(schema, table));
        }
    }
}


