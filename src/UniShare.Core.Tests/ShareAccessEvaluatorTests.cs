using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UniShare.Core;
using UniShare.Core.Models;

namespace UniShare.Core.Tests
{
    [TestClass]
    public class ShareAccessEvaluatorTests
    {
        private static ShareConfig CreateShare(bool allowGuest = false, ShareAccessLevel guestAccess = ShareAccessLevel.ReadWrite)
        {
            return new ShareConfig
            {
                ShareName = "movies",
                LocalPath = @"D:\Movies",
                AllowGuest = allowGuest,
                GuestAccess = guestAccess,
                Users = new List<ShareUserConfig>
                {
                    new ShareUserConfig { Username = "tv", Password = "111", AccessLevel = ShareAccessLevel.ReadOnly },
                    new ShareUserConfig { Username = "admin", Password = "222", AccessLevel = ShareAccessLevel.ReadWrite }
                }
            };
        }

        [TestMethod]
        public void ReadOnlyUser_CanRead_CannotWrite()
        {
            var share = CreateShare();
            Assert.IsTrue(ShareAccessEvaluator.Evaluate(share, "tv", FileAccess.Read, out _));
            Assert.IsFalse(ShareAccessEvaluator.Evaluate(share, "tv", FileAccess.Write, out string reason));
            Assert.IsFalse(string.IsNullOrEmpty(reason));
            Assert.IsFalse(ShareAccessEvaluator.Evaluate(share, "tv", FileAccess.ReadWrite, out _));
        }

        [TestMethod]
        public void ReadWriteUser_CanWrite()
        {
            Assert.IsTrue(ShareAccessEvaluator.Evaluate(CreateShare(), "admin", FileAccess.ReadWrite, out _));
        }

        [TestMethod]
        public void DomainQualifiedNames_AreNormalized()
        {
            var share = CreateShare();
            Assert.IsTrue(ShareAccessEvaluator.Evaluate(share, @"WORKGROUP\admin", FileAccess.Write, out _));
            Assert.IsTrue(ShareAccessEvaluator.Evaluate(share, "admin@workgroup", FileAccess.Write, out _));
            Assert.IsFalse(ShareAccessEvaluator.Evaluate(share, @"PC\tv", FileAccess.Write, out _));
        }

        [TestMethod]
        public void Guest_DeniedWhenGuestDisabled()
        {
            var share = CreateShare(allowGuest: false);
            Assert.IsFalse(ShareAccessEvaluator.Evaluate(share, "Guest", FileAccess.Read, out _));
            Assert.IsFalse(ShareAccessEvaluator.Evaluate(share, "", FileAccess.Read, out _));
        }

        [TestMethod]
        public void Guest_ReadOnlyGuestCannotWrite()
        {
            var share = CreateShare(allowGuest: true, guestAccess: ShareAccessLevel.ReadOnly);
            Assert.IsTrue(ShareAccessEvaluator.Evaluate(share, "Guest", FileAccess.Read, out _));
            Assert.IsFalse(ShareAccessEvaluator.Evaluate(share, "Guest", FileAccess.Write, out _));
        }

        [TestMethod]
        public void UnknownUser_FallsBackToGuestPermission()
        {
            Assert.IsFalse(ShareAccessEvaluator.Evaluate(CreateShare(allowGuest: false), "stranger", FileAccess.Read, out _));
            var guestShare = CreateShare(allowGuest: true, guestAccess: ShareAccessLevel.ReadOnly);
            Assert.IsTrue(ShareAccessEvaluator.Evaluate(guestShare, "stranger", FileAccess.Read, out _));
            Assert.IsFalse(ShareAccessEvaluator.Evaluate(guestShare, "stranger", FileAccess.Write, out _));
        }

        [TestMethod]
        public void LegacyAllowedUsers_RespectIsReadOnly()
        {
            var share = new ShareConfig { ShareName = "old", IsReadOnly = true, AllowedUsers = new List<string> { "bob" } };
            Assert.IsTrue(ShareAccessEvaluator.Evaluate(share, "bob", FileAccess.Read, out _));
            Assert.IsFalse(ShareAccessEvaluator.Evaluate(share, "bob", FileAccess.Write, out _));
        }

        [TestMethod]
        public void FindPasswordConflict_DetectsSameNameDifferentPassword()
        {
            var a = CreateShare();
            var b = new ShareConfig { ShareName = "photos" };
            var shares = new List<ShareConfig> { a, b };

            Assert.AreSame(a, ShareAccessEvaluator.FindPasswordConflict(shares, b, "TV", "999"));
            Assert.IsNull(ShareAccessEvaluator.FindPasswordConflict(shares, b, "tv", "111"));
            Assert.IsNull(ShareAccessEvaluator.FindPasswordConflict(shares, a, "tv", "999"), "编辑自身时不应与自身冲突");
            Assert.IsNull(ShareAccessEvaluator.FindPasswordConflict(shares, b, "newuser", "x"));
        }

        [TestMethod]
        public void BuildUserPasswordMap_ReportsConflictsAndKeepsFirst()
        {
            var config = new ServerConfig
            {
                Shares = new List<ShareConfig>
                {
                    new ShareConfig { ShareName = "a", Users = new List<ShareUserConfig> { new ShareUserConfig { Username = "tv", Password = "111" } } },
                    new ShareConfig { ShareName = "b", Users = new List<ShareUserConfig> { new ShareUserConfig { Username = "TV", Password = "222" } } },
                    new ShareConfig { ShareName = "c", Users = new List<ShareUserConfig> { new ShareUserConfig { Username = "pc", Password = "333" } } }
                }
            };

            var map = ShareAccessEvaluator.BuildUserPasswordMap(config, out var conflicts);
            Assert.AreEqual("111", map["tv"]);
            Assert.AreEqual("333", map["PC"]);
            CollectionAssert.AreEqual(new[] { "tv" }, conflicts);
        }

        [TestMethod]
        public void IsSmb1Required_FollowsShares()
        {
            var config = new ServerConfig { EnableSmb1 = true };
            Assert.IsTrue(ShareAccessEvaluator.IsSmb1Required(config), "无共享时沿用全局开关");
            config.Shares.Add(new ShareConfig { EnableSmb1 = false });
            Assert.IsFalse(ShareAccessEvaluator.IsSmb1Required(config));
            config.Shares.Add(new ShareConfig { EnableSmb1 = true });
            Assert.IsTrue(ShareAccessEvaluator.IsSmb1Required(config));
        }
    }
}
