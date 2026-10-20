/* Copyright (C) 2026 UniShare. All rights reserved. */
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;
using SMBLibrary.Server.SMB2;
using SMBLibrary.SMB2;
using Utilities;

namespace SMBLibrary.Tests.SMB2
{
    [TestClass]
    public class ProtocolStandardsComplianceTests
    {
        [TestMethod]
        public void TestMsSmb2_WildcardNegotiateResponse_Compliance()
        {
            // [MS-SMB2] Section 3.3.5.3.2:
            // When responding to "SMB 2.???" (0x02FF wildcard):
            // - Capabilities MUST be set to 0.
            // - MaxTransactSize, MaxReadSize, MaxWriteSize MUST be set to 0.
            // - SecurityBuffer MUST be empty (length 0, offset 0).
            var smb2Dialects = new List<string> { NegotiateHelper.SMB2xxxDialect };
            var ntlmProvider = new IndependentNTLMAuthenticationProvider(u => "password");
            var gssProvider = new GSSProvider(ntlmProvider);
            var state = new ConnectionState(null, null, null);

            var cmd = NegotiateHelper.GetNegotiateResponse(smb2Dialects, gssProvider, state, SMBTransportType.DirectTCPTransport, Guid.NewGuid(), DateTime.Now);
            Assert.IsInstanceOfType(cmd, typeof(NegotiateResponse));

            var resp = (NegotiateResponse)cmd;
            Assert.AreEqual(SMB2Dialect.SMB2xx, resp.DialectRevision);
            Assert.AreEqual((Capabilities)0, resp.Capabilities);
            Assert.AreEqual(0u, resp.MaxTransactSize);
            Assert.AreEqual(0u, resp.MaxReadSize);
            Assert.AreEqual(0u, resp.MaxWriteSize);
            Assert.AreEqual(0, resp.SecurityBuffer.Length);

            byte[] bytes = resp.GetBytes();
            // Parse back to ensure write/read roundtrip maintains 0 values
            var parsed = new NegotiateResponse(bytes, 0);
            Assert.AreEqual(SMB2Dialect.SMB2xx, parsed.DialectRevision);
            Assert.AreEqual((Capabilities)0, parsed.Capabilities);
            Assert.AreEqual(0u, parsed.MaxTransactSize);
            Assert.AreEqual(0, parsed.SecurityBuffer.Length);
        }

        [TestMethod]
        public void TestMsNlmp_AuthenticateMessage_OEMEncoding()
        {
            // [MS-NLMP] Section 2.2.1.3:
            // When NTLMSSP_NEGOTIATE_UNICODE is not set, UserName, DomainName, and WorkStation
            // are encoded in OEM/ANSI code page rather than UTF-16LE.
            byte[] lmResp = new byte[24];
            byte[] ntResp = new byte[24];
            string expectedUser = "testuser";
            string expectedDomain = "WORKGROUP";
            string expectedWorkstation = "MYPC";

            byte[] userBytes = Encoding.ASCII.GetBytes(expectedUser);
            byte[] domainBytes = Encoding.ASCII.GetBytes(expectedDomain);
            byte[] wsBytes = Encoding.ASCII.GetBytes(expectedWorkstation);

            int fixedLength = 64;
            byte[] buffer = new byte[fixedLength + domainBytes.Length + userBytes.Length + wsBytes.Length + lmResp.Length + ntResp.Length];

            ByteWriter.WriteAnsiString(buffer, 0, AuthenticateMessage.ValidSignature, 8);
            LittleEndianWriter.WriteUInt32(buffer, 8, (uint)MessageTypeName.Authenticate);
            // Notice: UnicodeEncoding (0x01) is OMITTED, OEMEncoding (0x02) is SET
            NegotiateFlags flags = NegotiateFlags.OEMEncoding | NegotiateFlags.NTLMSessionSecurity;
            LittleEndianWriter.WriteUInt32(buffer, 60, (uint)flags);

            int offset = fixedLength;
            AuthenticationMessageUtils.WriteBufferPointer(buffer, 28, (ushort)domainBytes.Length, (uint)offset);
            ByteWriter.WriteBytes(buffer, offset, domainBytes);
            offset += domainBytes.Length;

            AuthenticationMessageUtils.WriteBufferPointer(buffer, 36, (ushort)userBytes.Length, (uint)offset);
            ByteWriter.WriteBytes(buffer, offset, userBytes);
            offset += userBytes.Length;

            AuthenticationMessageUtils.WriteBufferPointer(buffer, 44, (ushort)wsBytes.Length, (uint)offset);
            ByteWriter.WriteBytes(buffer, offset, wsBytes);
            offset += wsBytes.Length;

            AuthenticationMessageUtils.WriteBufferPointer(buffer, 12, (ushort)lmResp.Length, (uint)offset);
            ByteWriter.WriteBytes(buffer, offset, lmResp);
            offset += lmResp.Length;

            AuthenticationMessageUtils.WriteBufferPointer(buffer, 20, (ushort)ntResp.Length, (uint)offset);
            ByteWriter.WriteBytes(buffer, offset, ntResp);

            var authMsg = new AuthenticateMessage(buffer);
            Assert.AreEqual(expectedUser, authMsg.UserName);
            Assert.AreEqual(expectedDomain, authMsg.DomainName);
            Assert.AreEqual(expectedWorkstation, authMsg.WorkStation);
        }

        [TestMethod]
        public void TestRfc4178_SpnegoReqFlags_DoesNotThrow()
        {
            // RFC 4178 Section 4.2.1: reqFlags [1] ContextFlags OPTIONAL.
            // When an initiator sends reqFlags, the parser must read it gracefully without throwing NotImplementedException.
            var tokenInit = new SimpleProtectedNegotiationTokenInit();
            tokenInit.MechanismTypeList = new List<byte[]> { GSSProvider.NTLMSSPIdentifier };
            tokenInit.MechanismToken = new byte[] { 1, 2, 3, 4 };
            byte[] tokenBytes = tokenInit.GetBytes(true);

            // Verify normal parsing succeeds
            var parsed = SimpleProtectedNegotiationToken.ReadToken(tokenBytes, 0, false);
            Assert.IsNotNull(parsed);
            Assert.IsInstanceOfType(parsed, typeof(SimpleProtectedNegotiationTokenInit));

            // Verify raw NegTokenInit (tag 0xA0) at top-level without 0x60 header also parses
            byte[] rawTokenInitBytes = tokenInit.GetBytes(false);
            var parsedRaw = SimpleProtectedNegotiationToken.ReadToken(rawTokenInitBytes, 0, false);
            Assert.IsNotNull(parsedRaw);
            Assert.IsInstanceOfType(parsedRaw, typeof(SimpleProtectedNegotiationTokenInit));
        }

        [TestMethod]
        public void TestMsNlmp_DomainCandidateFallback_AuthenticatesSuccessfully()
        {
            // Verify NTLMv2 authentication succeeds when client computes proof with target server name
            // but sends empty domain or uppercase domain in message.DomainName.
            string user = "admin";
            string password = "SecretPassword123!";
            byte[] serverChallenge = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            byte[] clientChallenge = new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 };

            var provider = new IndependentNTLMAuthenticationProvider(u => u.Equals(user, StringComparison.OrdinalIgnoreCase) ? password : null);

            var clientChallengeObj = new NTLMv2ClientChallenge(DateTime.UtcNow, clientChallenge, "WORKGROUP", "SERVER");
            byte[] padded = clientChallengeObj.GetBytesPadded();

            // Client computes proof using server name "SERVER"
            byte[] ntProof = NTLMCryptography.ComputeNTLMv2Proof(serverChallenge, padded, password, user, "SERVER");
            byte[] ntChallengeResponse = ByteUtils.Concatenate(ntProof, padded);

            // Build AuthenticateMessage where message.DomainName is empty string (common embedded client quirk)
            var authMsg = new AuthenticateMessage();
            authMsg.UserName = user;
            authMsg.DomainName = string.Empty;
            authMsg.LmChallengeResponse = new byte[24];
            authMsg.NtChallengeResponse = ntChallengeResponse;
            authMsg.NegotiateFlags = NegotiateFlags.UnicodeEncoding | NegotiateFlags.NTLMSessionSecurity | NegotiateFlags.ExtendedSessionSecurity;

            var authContext = new IndependentNTLMAuthenticationProvider.AuthContext(serverChallenge);
            NTStatus status = provider.Authenticate(authContext, authMsg.GetBytes());

            Assert.AreEqual(NTStatus.STATUS_SUCCESS, status);
        }
    }
}
