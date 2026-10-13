using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.AppLayer.Basic.DotnetExecutor.DTOS
{
    public class DotnetExecutionConditionDTO
    {
        /// <summary>
        /// 执行模式: singlefile 或 project
        /// </summary>
        public string Mode { get; set; } = "singlefile";

        /// <summary>
        /// 单文件路径（当mode=singlefile时使用）
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// 项目路径（当mode=project时使用）
        /// </summary>
        public string? ProjectPath { get; set; }

        /// <summary>
        /// 执行参数（每行一个）
        /// </summary>
        public string? Parameters { get; set; }

        /// <summary>
        /// 文件类型
        /// </summary>
        public string? FileType { get; set; }

        /// <summary>
        /// 上传的文件
        /// </summary>
        public List<IFormFile>? Files { get; set; }
    }
}
