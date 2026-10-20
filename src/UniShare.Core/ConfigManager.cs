using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using UniShare.Core.Models;

namespace UniShare.Core
{
    /// <summary>
    /// UniShare 服务配置管理器，负责 config.json 的持久化读写与默认配置生成
    /// </summary>
    public static class ConfigManager
    {
        public const string DefaultConfigFileName = "config.json";

        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 获取程序默认配置文件完整物理路径
        /// </summary>
        public static string GetDefaultConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultConfigFileName);
        }

        /// <summary>
        /// 从指定路径或默认路径加载配置；若文件不存在则自动生成一份带默认示例的配置文件
        /// </summary>
        public static ServerConfig Load(string? configPath = null)
        {
            string path = configPath ?? GetDefaultConfigPath();
            if (!File.Exists(path))
            {
                ServerConfig defaultConfig = CreateDefault();
                Save(defaultConfig, path);
                return defaultConfig;
            }

            try
            {
                string json = File.ReadAllText(path);
                ServerConfig? config = JsonSerializer.Deserialize<ServerConfig>(json, s_jsonOptions);
                if (config == null)
                {
                    throw new InvalidDataException("配置文件内容为空或格式无效。");
                }
                return config;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"读取配置文件 [{path}] 失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 将配置持久化保存到 JSON 文件
        /// </summary>
        public static void Save(ServerConfig config, string? configPath = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            string path = configPath ?? GetDefaultConfigPath();

            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(config, s_jsonOptions);
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
        }

        /// <summary>
        /// 生成开箱即用的默认配置（包含监控目录与媒体共享目录）
        /// </summary>
        public static ServerConfig CreateDefault()
        {
            return new ServerConfig
            {
                ListenPort = 445,
                Profile = ServerProfileType.CameraCompatibility,
                EnableSmb1 = false,
                EnableVerboseLog = true,
                AutoRestoreSystemServices = true,
                IsServiceEnabled = true,
                EnableNetworkDiscovery = true,
                Shares = new System.Collections.Generic.List<ShareConfig>(),
                Users = new System.Collections.Generic.List<VirtualUser>()
            };
        }
    }
}
