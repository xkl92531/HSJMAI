using System.Collections.Generic;

namespace MAF.DEMO.Core
{
    public class DbConnectionModel
    {
        public string ConnectionString { set; get; }
        public string DatabaseType { set; get; }
        public string ConfigId { set; get; }
    }
    /// <summary>
    /// 服务器配置类
    /// </summary>
    public class AppsettingsConfig
    {


        /// <summary>
        /// Redis地址
        /// </summary>
        public static string RedisServerUrl { get; set; }

        /// <summary>
        /// 允许访问的IP
        /// </summary>
        public static List<string> AllowedIpAddresses { set; get; }
        public static List<DbConnectionModel> DBList { set; get; }
        /// <summary>
        /// 默认数据库连接
        /// </summary>
        public static string DefaultConnectionString { get; set; } = "";

        /// <summary>
        /// 数据库类型SQLSERVER，MYSQL,ORACLE
        /// </summary>
        public static string DatabaseType { get; set; } = "SQLSERVER";


        /// <summary>
        /// 生产主键机器码
        /// </summary>
        public static int? MachineId { get; set; } = 1;

        /// <summary>
        /// 生产主键数据中心id
        /// </summary>
        public static int? DataCenterId { get; set; } = 1;


        /// <summary>
        /// 默认密码
        /// </summary>
        public static string DefaultPwd { get; set; }


        /// <summary>
        /// ApiService 配置
        /// </summary>
        public static List<ServiceApiHostModel> ServiceApiHosts { get; set; }


        /// <summary>
        /// JWT配置
        /// </summary>
        public static JWTConfig JWTConfig { get; set; }

        /// <summary>
        /// LLM配置
        /// </summary>
        public static LLMConfig LLMConfig { get; set; }

    }

    /// <summary>
    /// LLM配置类
    /// </summary>
    public class LLMConfig
    {
        /// <summary>
        /// 基础URL(VLLM推理服务地址)
        /// </summary>
        public string BaseUrl { get; set; } = "http://localhost:8000";

        /// <summary>
        /// 模型名称
        /// </summary>
        public string ModelName { get; set; } = "";

        /// <summary>
        /// API密钥
        /// </summary>
        public string ApiKey { get; set; } = "";

        /// <summary>
        /// 温度参数
        /// </summary>
        public float Temperature { get; set; } = 0.7f;

        /// <summary>
        /// 最大Token数
        /// </summary>
        public int MaxTokens { get; set; } = 2000;

        /// <summary>
        /// 超时时间(秒)
        /// </summary>
        public int TimeoutSeconds { get; set; } = 120;
    }






}
