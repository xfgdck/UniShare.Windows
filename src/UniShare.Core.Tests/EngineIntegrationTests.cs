using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SMBLibrary;
using SMBLibrary.Client;
using UniShare.Core;
using UniShare.Core.Models;
using FileAttributes = SMBLibrary.FileAttributes;

namespace UniShare.Core.Tests
{
    /// <summary>
    /// 端到端集成测试：在本机高位端口启动真实 SMB 引擎，使用 SMBLibrary 客户端验证认证、权限与热重载行为
    /// </summary>
    [TestClass]
    public class EngineIntegrationTests
    {
        private sealed class PortSmb2Client : SMB2Client
        {
            public bool ConnectToPort(int port) => Connect(IPAddress.Loopback, SMBTransportType.DirectTCPTransport, port);
        }

        private string m_root = string.Empty;
        private int m_port;
        private SmbServerEngine? m_engine;
        private ServerConfig m_config = new ServerConfig();

        [TestInitialize]
        public void Setup()
        {
            m_root = Path.Combine(Path.GetTempPath(), "UniShareIT_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(m_root, "movies"));
            Directory.CreateDirectory(Path.Combine(m_root, "photos"));
            File.WriteAllText(Path.Combine(m_root, "photos", "a.txt"), "hello");

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            m_port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            m_config = new ServerConfig
            {
                ListenPort = m_port,
                EnableNetworkDiscovery = false,
                EnableVerboseLog = false,
                Shares = new List<ShareConfig>
                {
                    new ShareConfig
                    {
                        ShareName = "movies",
                        LocalPath = Path.Combine(m_root, "movies"),
                        Users = new List<ShareUserConfig>
                        {
                            new ShareUserConfig { Username = "tv", Password = "111", AccessLevel = ShareAccessLevel.ReadOnly },
                            new ShareUserConfig { Username = "admin", Password = "222", AccessLevel = ShareAccessLevel.ReadWrite }
                        }
                    },
                    new ShareConfig
                    {
                        ShareName = "photos",
                        LocalPath = Path.Combine(m_root, "photos"),
                        AllowGuest = true,
                        GuestAccess = ShareAccessLevel.ReadOnly
                    }
                }
            };

            m_engine = new SmbServerEngine(ConfigManager.Clone(m_config));
            m_engine.Start();
        }

        [TestCleanup]
        public void Cleanup()
        {
            m_engine?.Stop();
            try { Directory.Delete(m_root, true); } catch { }
        }

        private PortSmb2Client LoginAs(string user, string password, out NTStatus status)
        {
            var client = new PortSmb2Client();
            Assert.IsTrue(client.ConnectToPort(m_port), "应能连接到测试服务端口");
            status = client.Login(string.Empty, user, password);
            return client;
        }

        private static NTStatus TryOpen(ISMBFileStore store, string path, bool write)
        {
            AccessMask access = write ? AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE : AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE;
            CreateDisposition disposition = write ? CreateDisposition.FILE_OVERWRITE_IF : CreateDisposition.FILE_OPEN;
            NTStatus status = store.CreateFile(out object handle, out _, path, access, FileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write, disposition, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            if (status == NTStatus.STATUS_SUCCESS)
            {
                store.CloseFile(handle);
            }
            return status;
        }

        [TestMethod]
        public void ReadWriteUser_CanWrite_ReadOnlyUser_IsDenied()
        {
            var admin = LoginAs("admin", "222", out NTStatus adminLogin);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, adminLogin);
            var adminStore = admin.TreeConnect("movies", out NTStatus tc);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, tc);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, TryOpen(adminStore, "new.txt", write: true));

            var tv = LoginAs("tv", "111", out NTStatus tvLogin);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, tvLogin);
            var tvStore = tv.TreeConnect("movies", out tc);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, tc);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, TryOpen(tvStore, "new.txt", write: false));
            Assert.AreEqual(NTStatus.STATUS_ACCESS_DENIED, TryOpen(tvStore, "new.txt", write: true));

            admin.Disconnect();
            tv.Disconnect();
        }

        [TestMethod]
        public void WrongPassword_IsRejected()
        {
            var client = LoginAs("admin", "wrong", out NTStatus status);
            Assert.AreEqual(NTStatus.STATUS_LOGON_FAILURE, status);
            client.Disconnect();
        }

        [TestMethod]
        public void UnknownUser_FallsBackToGuest_WhenAnyShareAllowsGuest()
        {
            var client = LoginAs("stranger", "whatever", out NTStatus status);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, status, "存在开放匿名的共享时，未知用户应降级为来宾会话而不是登录失败");

            var photos = client.TreeConnect("photos", out NTStatus tc);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, tc);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, TryOpen(photos, "a.txt", write: false));
            Assert.AreEqual(NTStatus.STATUS_ACCESS_DENIED, TryOpen(photos, "a.txt", write: true), "匿名只读");

            var movies = client.TreeConnect("movies", out tc);
            if (tc == NTStatus.STATUS_SUCCESS)
            {
                Assert.AreNotEqual(NTStatus.STATUS_SUCCESS, TryOpen(movies, "x.txt", write: false), "来宾不得访问未开放匿名的共享");
            }
            client.Disconnect();
        }

        [TestMethod]
        public void UnknownUser_IsRejected_WhenNoShareAllowsGuest()
        {
            m_config.Shares[1].AllowGuest = false;
            m_config.Shares[1].Users.Add(new ShareUserConfig { Username = "admin", Password = "222" });
            Assert.IsTrue(m_engine!.ReloadConfig(ConfigManager.Clone(m_config)));

            var client = LoginAs("stranger", "whatever", out NTStatus status);
            Assert.AreEqual(NTStatus.STATUS_LOGON_FAILURE, status);
            client.Disconnect();
        }

        [TestMethod]
        public void HotReload_AppliesToAlreadyConnectedClients()
        {
            var admin = LoginAs("admin", "222", out _);
            var store = admin.TreeConnect("movies", out NTStatus tc);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, tc);
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, TryOpen(store, "f.txt", write: true));

            // 1. 改为只读：同一个已建立的连接立即失去写权限
            m_config.Shares[0].Users[1].AccessLevel = ShareAccessLevel.ReadOnly;
            Assert.IsTrue(m_engine!.ReloadConfig(ConfigManager.Clone(m_config)));
            Assert.AreEqual(NTStatus.STATUS_ACCESS_DENIED, TryOpen(store, "f.txt", write: true));
            Assert.AreEqual(NTStatus.STATUS_SUCCESS, TryOpen(store, "f.txt", write: false));

            // 2. 移除共享：已建立的连接也无法继续访问
            m_config.Shares.RemoveAt(0);
            Assert.IsTrue(m_engine.ReloadConfig(ConfigManager.Clone(m_config)));
            Assert.AreEqual(NTStatus.STATUS_ACCESS_DENIED, TryOpen(store, "f.txt", write: false));

            admin.Disconnect();
        }

        [TestMethod]
        public void Reload_ReturnsFalse_WhenSmb1SwitchChanges()
        {
            m_config.Shares[0].EnableSmb1 = true;
            Assert.IsFalse(m_engine!.ReloadConfig(ConfigManager.Clone(m_config)), "SMB1 开关变化必须重启服务才能生效");
        }
    }
}
