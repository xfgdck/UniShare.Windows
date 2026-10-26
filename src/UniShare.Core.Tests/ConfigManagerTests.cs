using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UniShare.Core;
using UniShare.Core.Models;

namespace UniShare.Core.Tests
{
    [TestClass]
    public class ConfigManagerTests
    {
        private string m_dir = string.Empty;
        private string m_path = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            m_dir = Path.Combine(Path.GetTempPath(), "UniShareTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(m_dir);
            m_path = Path.Combine(m_dir, "config.json");
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(m_dir, true); } catch { }
        }

        [TestMethod]
        public void Dpapi_RoundTrip()
        {
            string protectedValue = DpapiHelper.Protect("p@ss 中文");
            Assert.AreNotEqual("p@ss 中文", protectedValue);
            Assert.IsTrue(DpapiHelper.TryUnprotect(protectedValue, out string plain));
            Assert.AreEqual("p@ss 中文", plain);
            Assert.IsFalse(DpapiHelper.TryUnprotect("bm90LXZhbGlk", out _));
        }

        [TestMethod]
        public void Save_EncryptsPasswords_AndLoadDecrypts()
        {
            var config = ConfigManager.CreateDefault();
            config.Shares.Add(new ShareConfig
            {
                ShareName = "movies",
                LocalPath = @"D:\Movies",
                Users = new List<ShareUserConfig> { new ShareUserConfig { Username = "tv", Password = "SuperSecret123" } }
            });
            config.SystemSmbBackup = new SystemSmbState { LanmanStartType = 3, WasRunning = false };

            ConfigManager.Save(config, m_path);

            string json = File.ReadAllText(m_path);
            Assert.IsFalse(json.Contains("SuperSecret123"), "磁盘上不应出现明文密码");
            Assert.IsTrue(json.Contains("PasswordProtected"));
            Assert.AreEqual("SuperSecret123", config.Shares[0].Users[0].Password, "保存不应修改内存中的配置");

            var loaded = ConfigManager.Load(m_path);
            Assert.AreEqual("SuperSecret123", loaded.Shares[0].Users[0].Password);
            Assert.IsNull(loaded.Shares[0].Users[0].PasswordProtected);
            Assert.AreEqual(3, loaded.SystemSmbBackup!.LanmanStartType);
            Assert.IsFalse(loaded.SystemSmbBackup.WasRunning);
        }

        [TestMethod]
        public void Load_LegacyPlaintext_IsReadAndReEncrypted()
        {
            File.WriteAllText(m_path, @"{ ""Shares"": [ { ""ShareName"": ""a"", ""LocalPath"": ""D:\\A"", ""Users"": [ { ""Username"": ""u"", ""Password"": ""legacyPwd"" } ] } ] }");

            var loaded = ConfigManager.Load(m_path);
            Assert.AreEqual("legacyPwd", loaded.Shares[0].Users[0].Password);
            Assert.IsFalse(File.ReadAllText(m_path).Contains("legacyPwd"), "旧版明文密码应在加载后被加密重写");
        }

        [TestMethod]
        public void LoadOrRecover_CorruptFile_BacksUpAndUsesDefault()
        {
            File.WriteAllText(m_path, "{ this is not json");

            var config = ConfigManager.LoadOrRecover(m_path, out string? message);
            Assert.IsNotNull(message);
            Assert.AreEqual(0, config.Shares.Count);
            Assert.IsTrue(Directory.GetFiles(m_dir, "config.json.corrupt-*.bak").Any());
            Assert.IsTrue(File.Exists(m_path));
        }

        [TestMethod]
        public void Load_UndecryptablePassword_ProducesWarning()
        {
            File.WriteAllText(m_path, @"{ ""Shares"": [ { ""ShareName"": ""a"", ""LocalPath"": ""D:\\A"", ""Users"": [ { ""Username"": ""tv"", ""PasswordProtected"": ""bm90LXZhbGlk"" } ] } ] }");

            var config = ConfigManager.LoadOrRecover(m_path, out string? message);
            Assert.AreEqual(string.Empty, config.Shares[0].Users[0].Password);
            Assert.IsNotNull(message);
            StringAssert.Contains(message, "tv");
            Assert.AreEqual(1, config.Shares.Count, "密码无法解密不应被当作配置损坏而重置");

            File.WriteAllText(m_path, @"{ ""Shares"": [] }");
            ConfigManager.LoadOrRecover(m_path, out message);
            Assert.IsNull(message, "正常加载后不应残留上一次的警告");
        }

        [TestMethod]
        public void Clone_IsDeepCopy()
        {
            var config = ConfigManager.CreateDefault();
            config.Shares.Add(new ShareConfig { ShareName = "x", Users = new List<ShareUserConfig> { new ShareUserConfig { Username = "u", Password = "p" } } });

            var clone = ConfigManager.Clone(config);
            clone.Shares[0].ShareName = "y";
            clone.Shares.Add(new ShareConfig());

            Assert.AreEqual("x", config.Shares[0].ShareName);
            Assert.AreEqual(1, config.Shares.Count);
            Assert.AreEqual("p", clone.Shares[0].Users[0].Password);
        }
    }
}
