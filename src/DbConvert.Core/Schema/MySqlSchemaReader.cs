using System;
using System.Collections.Generic;
using System.Data.Common;

namespace DbConvert.Schema
{
    /// <summary>MySQL / TiDB / OceanBase(MySQL 模式) / GBase 8a 等的结构读取。</summary>
    public class MySqlSchemaReader : ISchemaReader
    {
        public string ResolveSchema(DbConnection conn, string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            object v = SchemaSql.Scalar(conn, "SELECT DATABASE()");
            return v == null ? "" : Convert.ToString(v);
        }

        public List<TableDef> ReadTables(DbConnection conn, string schema, Action<string> log)
        {
            var tables = new List<TableDef>();
            if (string.IsNullOrEmpty(schema)) throw new InvalidOperationException("MySQL 需要指定数据库名（schema）");

            string litSchema = SqlUtil.Literal(schema);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT TABLE_NAME, TABLE_COMMENT, TABLE_ROWS FROM information_schema.TABLES " +
                    "WHERE TABLE_SCHEMA = " + litSchema + " AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME";
                cmd.CommandTimeout = 120;
                using (var rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        var t = new TableDef { Schema = schema, Name = Convert.ToString(rd[0]) };
                        if (rd[1] != DBNull.Value) t.Comment = Convert.ToString(rd[1]);
                        if (rd[2] != DBNull.Value) t.RowCountEstimate = Convert.ToInt64(rd[2]);
                        tables.Add(t);
                    }
                }
            }

            var byName = new Dictionary<string, TableDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables) byName[t.Name] = t;

            // 列
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION, COLUMN_DEFAULT, IS_NULLABLE, DATA_TYPE, " +
                        "CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION, " +
                        "COLUMN_TYPE, EXTRA, COLUMN_COMMENT, CHARACTER_SET_NAME, COLLATION_NAME, GENERATION_EXPRESSION " +
                        "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = " + litSchema +
                        " ORDER BY TABLE_NAME, ORDINAL_POSITION";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tableName = acc.Str("TABLE_NAME");
                            if (tableName == null || !byName.ContainsKey(tableName)) continue;
                            TableDef t = byName[tableName];

                            var col = new ColumnDef();
                            col.Name = acc.Str("COLUMN_NAME") ?? "";
                            col.TargetName = col.Name;
                            col.Ordinal = acc.Int("ORDINAL_POSITION") ?? 0;
                            col.Nullable = !string.Equals(acc.Str("IS_NULLABLE"), "NO", StringComparison.OrdinalIgnoreCase);
                            col.Comment = acc.Str("COLUMN_COMMENT") ?? "";
                            string extra = (acc.Str("EXTRA") ?? "").ToLowerInvariant();
                            col.IsIdentity = extra.Contains("auto_increment");
                            // MySQL 8 会把 DEFAULT CURRENT_TIMESTAMP 也标成 DEFAULT_GENERATED，不能据此判定计算列
                            col.IsComputed = extra.Contains("virtual generated") || extra.Contains("stored generated");
                            MapType(col, acc);
                            col.Charset = acc.Str("CHARACTER_SET_NAME") ?? "";
                            col.Collation = acc.Str("COLLATION_NAME") ?? "";

                            string gen = acc.Str("GENERATION_EXPRESSION");
                            if (!string.IsNullOrWhiteSpace(gen))
                            {
                                col.IsComputed = true;
                                col.GenerationExpression = gen.Trim();
                                col.GeneratedStored = extra.Contains("stored");
                            }
                            else if (col.IsComputed)
                            {
                                col.IsComputed = false;
                                col.MappingWarning = "生成列没读到表达式，已按普通列处理";
                            }

                            string def = acc.Str("COLUMN_DEFAULT");
                            if (def != null && !string.Equals(def, "NULL", StringComparison.OrdinalIgnoreCase))
                            {
                                col.HasDefault = true;
                                col.DefaultExpression = NormalizeDefault(def.Trim(), col);
                            }
                            t.Columns.Add(col);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 MySQL 列信息失败：" + ex.Message);
            }

            // 索引 / 主键
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME FROM information_schema.STATISTICS " +
                        "WHERE TABLE_SCHEMA = " + litSchema + " ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tableName = acc.Str("TABLE_NAME");
                            if (tableName == null || !byName.ContainsKey(tableName)) continue;
                            TableDef t = byName[tableName];
                            string indexName = acc.Str("INDEX_NAME") ?? "";
                            IndexDef ix = null;
                            foreach (var e in t.Indexes)
                            {
                                if (string.Equals(e.Name, indexName, StringComparison.OrdinalIgnoreCase)) { ix = e; break; }
                            }
                            if (ix == null)
                            {
                                ix = new IndexDef { Name = indexName };
                                ix.IsUnique = !acc.Int("NON_UNIQUE").HasValue || acc.Int("NON_UNIQUE").Value == 0;
                                ix.IsPrimary = string.Equals(indexName, "PRIMARY", StringComparison.OrdinalIgnoreCase);
                                t.Indexes.Add(ix);
                            }
                            string c = acc.Str("COLUMN_NAME");
                            if (!string.IsNullOrEmpty(c)) ix.Columns.Add(new IndexColumn { Name = c });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 MySQL 索引失败（不影响建表和数据搬运，但不会创建索引）：" + ex.Message);
            }

            // 外键
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT k.TABLE_NAME, k.CONSTRAINT_NAME, k.COLUMN_NAME, k.REFERENCED_TABLE_SCHEMA, " +
                        "k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME, k.ORDINAL_POSITION, r.UPDATE_RULE, r.DELETE_RULE " +
                        "FROM information_schema.KEY_COLUMN_USAGE k " +
                        "JOIN information_schema.REFERENTIAL_CONSTRAINTS r ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA " +
                        " AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND r.TABLE_NAME = k.TABLE_NAME " +
                        "WHERE k.TABLE_SCHEMA = " + litSchema + " AND k.REFERENCED_TABLE_NAME IS NOT NULL " +
                        "ORDER BY k.TABLE_NAME, k.CONSTRAINT_NAME, k.ORDINAL_POSITION";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("TABLE_NAME");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            TableDef t = byName[tn];
                            string fkName = acc.Str("CONSTRAINT_NAME") ?? "";
                            ForeignKeyDef fk = null;
                            foreach (var e in t.ForeignKeys)
                            {
                                if (string.Equals(e.Name, fkName, StringComparison.OrdinalIgnoreCase)) { fk = e; break; }
                            }
                            if (fk == null)
                            {
                                fk = new ForeignKeyDef
                                {
                                    Name = fkName,
                                    RefSchema = acc.Str("REFERENCED_TABLE_SCHEMA") ?? "",
                                    RefTable = acc.Str("REFERENCED_TABLE_NAME") ?? "",
                                    OnUpdate = acc.Str("UPDATE_RULE") ?? "",
                                    OnDelete = acc.Str("DELETE_RULE") ?? ""
                                };
                                t.ForeignKeys.Add(fk);
                            }
                            string c = acc.Str("COLUMN_NAME");
                            if (!string.IsNullOrEmpty(c)) fk.Columns.Add(c);
                            string rc = acc.Str("REFERENCED_COLUMN_NAME");
                            if (!string.IsNullOrEmpty(rc)) fk.RefColumns.Add(rc);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 MySQL 外键失败（将不迁移外键）：" + ex.Message);
            }

            // CHECK 约束（MySQL 8.0.16+）
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT tc.TABLE_NAME, cc.CONSTRAINT_NAME, cc.CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS cc " +
                        "JOIN information_schema.TABLE_CONSTRAINTS tc ON tc.CONSTRAINT_SCHEMA = cc.CONSTRAINT_SCHEMA " +
                        " AND tc.CONSTRAINT_NAME = cc.CONSTRAINT_NAME " +
                        "WHERE cc.CONSTRAINT_SCHEMA = " + litSchema;
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("TABLE_NAME");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            byName[tn].Checks.Add(new CheckDef
                            {
                                Name = acc.Str("CONSTRAINT_NAME") ?? "",
                                Expression = acc.Str("CHECK_CLAUSE") ?? ""
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 MySQL CHECK 约束失败（MySQL 8.0.16 以下没有该视图）：" + ex.Message);
            }

            foreach (var t in tables)
            {
                if (t.Indexes.Count == 0 && t.Columns.Count > 0)
                {
                    log?.Invoke("表 " + t.QualifiedName + " 没有读到索引/主键信息");
                }
            }

            return tables;
        }

        private void MapType(ColumnDef col, RowAccess acc)
        {
            string dt = (acc.Str("DATA_TYPE") ?? "").Trim().ToLowerInvariant();
            string ct = (acc.Str("COLUMN_TYPE") ?? "").Trim().ToLowerInvariant();
            long? charLen = acc.Long("CHARACTER_MAXIMUM_LENGTH");
            col.IsUnsigned = ct.Contains("unsigned");
            col.RawType = string.IsNullOrEmpty(ct) ? dt : ct;
            col.SourceTypeName = col.RawType;

            int? numericPrecision = acc.Int("NUMERIC_PRECISION");
            int? numericScale = acc.Int("NUMERIC_SCALE");
            int? dtPrecision = acc.Int("DATETIME_PRECISION");

            switch (dt)
            {
                case "tinyint":
                    col.Kind = ct.StartsWith("tinyint(1)") ? ColumnKind.Boolean : ColumnKind.TinyInt;
                    break;
                case "smallint":
                    col.Kind = ColumnKind.SmallInt;
                    break;
                case "mediumint":
                case "int":
                case "integer":
                    col.Kind = ColumnKind.Int;
                    break;
                case "bigint":
                    col.Kind = ColumnKind.BigInt;
                    break;
                case "decimal":
                case "numeric":
                    col.Kind = ColumnKind.Decimal;
                    col.Precision = numericPrecision;
                    col.Scale = numericScale;
                    break;
                case "float":
                    col.Kind = ColumnKind.Float;
                    break;
                case "double":
                case "double precision":
                case "real":
                    col.Kind = ColumnKind.Double;
                    break;
                case "bit":
                    if (charLen.HasValue && charLen.Value == 1) col.Kind = ColumnKind.Boolean;
                    else col.Kind = ColumnKind.BigInt;
                    break;
                case "char":
                    col.Kind = ColumnKind.Char;
                    col.Length = ClampLen(charLen);
                    break;
                case "varchar":
                    col.Kind = ColumnKind.VarChar;
                    col.Length = ClampLen(charLen);
                    break;
                case "tinytext":
                case "text":
                case "mediumtext":
                case "longtext":
                    col.Kind = ColumnKind.Text;
                    break;
                case "binary":
                    col.Kind = ColumnKind.Binary;
                    col.Length = ClampLen(charLen);
                    break;
                case "varbinary":
                    col.Kind = ColumnKind.Binary;
                    col.Length = ClampLen(charLen);
                    break;
                case "tinyblob":
                case "blob":
                case "mediumblob":
                case "longblob":
                    col.Kind = ColumnKind.Blob;
                    break;
                case "date":
                    col.Kind = ColumnKind.Date;
                    break;
                case "time":
                    col.Kind = ColumnKind.Time;
                    col.DateTimePrecision = dtPrecision;
                    break;
                case "datetime":
                case "timestamp":
                    col.Kind = ColumnKind.DateTime;
                    col.DateTimePrecision = dtPrecision ?? 0;
                    break;
                case "year":
                    col.Kind = ColumnKind.SmallInt;
                    break;
                case "json":
                    col.Kind = ColumnKind.Json;
                    break;
                case "enum":
                case "set":
                    col.Kind = ColumnKind.Enum;
                    break;
                case "uuid":
                    col.Kind = ColumnKind.Guid;
                    break;
                case "geometry":
                case "point":
                case "linestring":
                case "polygon":
                case "multipoint":
                case "multilinestring":
                case "multipolygon":
                case "geometrycollection":
                    col.Kind = ColumnKind.Unknown;
                    col.MappingWarning = "空间/几何类型暂不支持自动迁移";
                    break;
                default:
                    col.Kind = ColumnKind.Unknown;
                    col.MappingWarning = "未识别的 MySQL 类型 " + dt;
                    break;
            }
        }

        private static int? ClampLen(long? len)
        {
            if (!len.HasValue || len.Value <= 0) return null;
            if (len.Value > 1000000) return 1000000;
            return (int)len.Value;
        }

        /// <summary>information_schema 返回的默认值不带引号，字符串/日期要补回引号；函数表达式保持原样。</summary>
        private static string NormalizeDefault(string def, ColumnDef col)
        {
            string u = (def ?? "").Trim();
            if (u.Length == 0) return "''";
            switch (col.Kind)
            {
                case ColumnKind.Char:
                case ColumnKind.VarChar:
                case ColumnKind.Text:
                case ColumnKind.NChar:
                case ColumnKind.NVarChar:
                case ColumnKind.Enum:
                case ColumnKind.Json:
                case ColumnKind.Xml:
                    if (u.StartsWith("'") || u.StartsWith("_utf8") || u.StartsWith("(")) return u;
                    return SqlUtil.Literal(u);
                case ColumnKind.Date:
                case ColumnKind.DateTime:
                case ColumnKind.Time:
                    if (u.IndexOf("CURRENT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        u.IndexOf("NOW", StringComparison.OrdinalIgnoreCase) >= 0 || u.StartsWith("("))
                        return u;
                    return SqlUtil.Literal(u);
                default:
                    return u;
            }
        }

        public List<ViewDef> ReadViews(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<ViewDef>();
            if (string.IsNullOrEmpty(schema)) return list;
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT v.TABLE_NAME, v.VIEW_DEFINITION, t.TABLE_COMMENT FROM information_schema.VIEWS v " +
                        "LEFT JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = v.TABLE_SCHEMA AND t.TABLE_NAME = v.TABLE_NAME " +
                        "WHERE v.TABLE_SCHEMA = " + SqlUtil.Literal(schema) + " ORDER BY v.TABLE_NAME";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            list.Add(new ViewDef
                            {
                                Schema = schema,
                                Name = acc.Str("TABLE_NAME") ?? "",
                                Definition = acc.Str("VIEW_DEFINITION") ?? "",
                                Comment = acc.Str("TABLE_COMMENT") ?? ""
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 MySQL 视图失败：" + ex.Message);
            }
            return list;
        }

        public List<RoutineDef> ReadRoutines(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<RoutineDef>();
            if (string.IsNullOrEmpty(schema)) return list;
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT ROUTINE_NAME, ROUTINE_TYPE, DTD_IDENTIFIER, ROUTINE_DEFINITION, ROUTINE_COMMENT " +
                        "FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = " + SqlUtil.Literal(schema) +
                        " ORDER BY ROUTINE_TYPE, ROUTINE_NAME";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            list.Add(new RoutineDef
                            {
                                Schema = schema,
                                Name = acc.Str("ROUTINE_NAME") ?? "",
                                Kind = (acc.Str("ROUTINE_TYPE") ?? "PROCEDURE").ToLowerInvariant(),
                                ReturnsType = acc.Str("DTD_IDENTIFIER") ?? "",
                                Definition = acc.Str("ROUTINE_DEFINITION") ?? "",
                                Arguments = acc.Str("ROUTINE_COMMENT") ?? ""
                            });
                        }
                    }
                }

                // information_schema.ROUTINES 只有过程正文；SHOW CREATE 才带参数、返回类型和完整可执行 DDL
                foreach (RoutineDef r in list)
                {
                    try
                    {
                        string keyword = r.Kind == "function" ? "FUNCTION" : "PROCEDURE";
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SHOW CREATE " + keyword + " " + Qi(schema) + "." + Qi(r.Name);
                            cmd.CommandTimeout = 120;
                            using (var rd = cmd.ExecuteReader())
                            {
                                if (rd.Read())
                                {
                                    string full = FindCreateColumn(rd);
                                    if (!string.IsNullOrWhiteSpace(full)) r.Definition = full;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke("读取 " + r.Kind + " " + r.Name + " 完整 DDL 失败，改用正文：" + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 MySQL 存储过程/函数失败：" + ex.Message);
            }
            return list;
        }

        public List<TriggerDef> ReadTriggers(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<TriggerDef>();
            if (string.IsNullOrEmpty(schema)) return list;
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT TRIGGER_NAME, EVENT_MANIPULATION, ACTION_TIMING, EVENT_OBJECT_TABLE, ACTION_STATEMENT " +
                        "FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = " + SqlUtil.Literal(schema) +
                        " ORDER BY EVENT_OBJECT_TABLE, ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORDER";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            list.Add(new TriggerDef
                            {
                                Schema = schema,
                                Name = acc.Str("TRIGGER_NAME") ?? "",
                                Table = acc.Str("EVENT_OBJECT_TABLE") ?? "",
                                Timing = (acc.Str("ACTION_TIMING") ?? "").ToUpperInvariant(),
                                Event = (acc.Str("EVENT_MANIPULATION") ?? "").ToUpperInvariant(),
                                Definition = acc.Str("ACTION_STATEMENT") ?? ""
                            });
                        }
                    }
                }

                // ACTION_STATEMENT 只有触发器正文；SHOW CREATE TRIGGER 带 CREATE/ON/FOR EACH ROW 完整语句
                foreach (TriggerDef tr in list)
                {
                    try
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "SHOW CREATE TRIGGER " + Qi(schema) + "." + Qi(tr.Name);
                            cmd.CommandTimeout = 120;
                            using (var rd = cmd.ExecuteReader())
                            {
                                if (rd.Read())
                                {
                                    string full = FindCreateColumn(rd);
                                    if (!string.IsNullOrWhiteSpace(full)) tr.Definition = full;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke("读取触发器 " + tr.Name + " 完整 DDL 失败，改用正文：" + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 MySQL 触发器失败：" + ex.Message);
            }
            return list;
        }

        private static string Qi(string name)
        {
            return "`" + (name ?? "").Replace("`", "``") + "`";
        }

        /// <summary>SHOW CREATE 的“Create Procedure / Create Function / SQL Original Statement”列。</summary>
        private static string FindCreateColumn(DbDataReader rd)
        {
            for (int i = 0; i < rd.FieldCount; i++)
            {
                string col = rd.GetName(i);
                if (col.StartsWith("Create ", StringComparison.OrdinalIgnoreCase) ||
                    col.Equals("SQL Original Statement", StringComparison.OrdinalIgnoreCase))
                {
                    return rd[i] == DBNull.Value ? "" : Convert.ToString(rd[i]);
                }
            }
            return rd.FieldCount > 2 && rd[2] != DBNull.Value ? Convert.ToString(rd[2]) : "";
        }

        public long CountRows(DbConnection conn, string schema, string table)
        {
            var d = Dialects.Get("mysql");
            return SchemaSql.CountRows(conn, d.Qualify(schema, table));
        }
    }
}
