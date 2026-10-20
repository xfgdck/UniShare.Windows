using System;
using System.Collections.Generic;

using System.Text.Json.Serialization;

namespace UniShare.Core.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ServerProfileType
    {
        /// <summary>
        /// 摄像头极限兼容模式：放开 SMB 1.0、放开 NTLMv1、关闭强制签名、支持匿名读写
        /// </summary>
        CameraCompatibility = 0,

        /// <summary>
        /// 局域网高速共享模式：启用 SMB 2.x/3.x、大缓冲区吞吐、流媒体 Range 读支持
        /// </summary>
        GeneralLanSharing = 1
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ShareAccessLevel
    {
        ReadOnly = 0,    // 只读
        ReadWrite = 1    // 读写
    }

    public class ShareUserConfig
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public ShareAccessLevel AccessLevel { get; set; } = ShareAccessLevel.ReadWrite;
    }

    public class UserPermissionConfig
    {
        public string Username { get; set; } = string.Empty;
        public ShareAccessLevel AccessLevel { get; set; } = ShareAccessLevel.ReadWrite;
    }

    public class ShareConfig
    {
        /// <summary>
        /// 共享名称（例如 camera, movies, photos）
        /// </summary>
        public string ShareName { get; set; } = "share";

        /// <summary>
        /// 本地磁盘实际物理路径
        /// </summary>
        public string LocalPath { get; set; } = string.Empty;

        /// <summary>
        /// 是否为只读共享（全局兼容标志）
        /// </summary>
        public bool IsReadOnly { get; set; } = false;

        /// <summary>
        /// 是否允许匿名 / Guest 来宾访问
        /// </summary>
        public bool AllowGuest { get; set; } = false;

        /// <summary>
        /// 来宾具体访问权限（只读或读写）
        /// </summary>
        public ShareAccessLevel GuestAccess { get; set; } = ShareAccessLevel.ReadWrite;

        /// <summary>
        /// 当前共享目录专属的独立用户列表（每个目录独立，不跨目录共有）
        /// </summary>
        public List<ShareUserConfig> Users { get; set; } = new List<ShareUserConfig>();

        /// <summary>
        /// 独立用户的具体访问权限配置列表（兼容旧版数据格式）
        /// </summary>
        public List<UserPermissionConfig> UserPermissions { get; set; } = new List<UserPermissionConfig>();

        /// <summary>
        /// 授权访问的用户列表（为空表示允许所有有效虚拟用户访问）
        /// </summary>
        public List<string> AllowedUsers { get; set; } = new List<string>();

        /// <summary>
        /// 是否开启 SMB 1.0 支持（兼容旧协议设备）
        /// </summary>
        public bool EnableSmb1 { get; set; } = false;

        /// <summary>
        /// 共享描述信息
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    public class VirtualUser
    {
        /// <summary>
        /// 用户名
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// 密码
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// 是否为来宾账号
        /// </summary>
        public bool IsGuest { get; set; } = false;

        /// <summary>
        /// 备注说明
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    public class ServerConfig
    {
        /// <summary>
        /// 监听端口（默认 445）
        /// </summary>
        public int ListenPort { get; set; } = 445;

        /// <summary>
        /// 运行模式（CameraCompatibility: 摄像头兼容 / GeneralLanSharing: 局域网共享）
        /// </summary>
        public ServerProfileType Profile { get; set; } = ServerProfileType.CameraCompatibility;

        /// <summary>
        /// 是否开启 SMB 1.0 支持（兼容旧协议设备；SMB 2.0/3.0 始终开启）
        /// </summary>
        public bool EnableSmb1 { get; set; } = false;

        /// <summary>
        /// 是否开启详细日志
        /// </summary>
        public bool EnableVerboseLog { get; set; } = true;

        /// <summary>
        /// 退出时是否提示还原 Windows 自带 LanmanServer 服务
        /// </summary>
        public bool AutoRestoreSystemServices { get; set; } = true;

        /// <summary>
        /// 记录上次服务的开启/关闭状态，软件重启后自动恢复该状态
        /// </summary>
        public bool IsServiceEnabled { get; set; } = true;

        /// <summary>
        /// 是否启用开机自动启动
        /// </summary>
        public bool AutoStart { get; set; } = false;

        /// <summary>
        /// 开机自启动时是否静默最小化到系统托盘
        /// </summary>
        public bool StartMinimized { get; set; } = true;

        /// <summary>
        /// 是否启用局域网设备自发现（mDNS 与 WS-Discovery 广播）
        /// </summary>
        public bool EnableNetworkDiscovery { get; set; } = true;

        /// <summary>
        /// 挂载的共享目录列表
        /// </summary>
        public List<ShareConfig> Shares { get; set; } = new List<ShareConfig>();

        /// <summary>
        /// 虚拟用户列表
        /// </summary>
        public List<VirtualUser> Users { get; set; } = new List<VirtualUser>();
    }
}
