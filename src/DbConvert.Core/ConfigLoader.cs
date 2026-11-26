using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DbConvert
{
    public static class ConfigLoader
    {
        private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static MigrationConfig Load(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到配置文件 " + path);
            string json = File.ReadAllText(path, Encoding.UTF8);
            MigrationConfig cfg = JsonSerializer.Deserialize<MigrationConfig>(json, ReadOptions);
            if (cfg == null) throw new InvalidOperationException("配置文件解析为空：" + path);
            if (cfg.Source == null || cfg.Target == null) throw new InvalidOperationException("配置文件缺少 source 或 target");
            if (cfg.Options == null) cfg.Options = new OptionsConfig();
            if (cfg.Tables == null) cfg.Tables = new System.Collections.Generic.List<TableMapConfig>();
            return cfg;
        }

        public static void Save(MigrationConfig cfg, string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, WriteOptions), new UTF8Encoding(true));
        }

        public static MigrationConfig Sample(string sourceKind, string targetKind)
        {
            var cfg = new MigrationConfig();
            cfg.Source.Kind = sourceKind ?? "mysql";
            cfg.Target.Kind = targetKind ?? "dm";
            cfg.Source.Host = "127.0.0.1";
            cfg.Source.Port = ConnectionStrings.DefaultPort(cfg.Source.Kind);
            cfg.Target.Host = "127.0.0.1";
            cfg.Target.Port = ConnectionStrings.DefaultPort(cfg.Target.Kind);
            cfg.Target.Schema = "";
            cfg.Options.Mode = "both";
            cfg.Options.BatchSize = 2000;
            cfg.Options.Parallel = 1;
            cfg.Options.DropExisting = false;
            cfg.Options.CreateIndexes = true;
            cfg.Options.CreateIndexesAfterLoad = true;
            cfg.Options.IdentifierCase = "preserve";
            cfg.Options.VarcharLengthMode = "auto";
            cfg.Options.VerifyMode = "checksum";
            return cfg;
        }
    }
}
