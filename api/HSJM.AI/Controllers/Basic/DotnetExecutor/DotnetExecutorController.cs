using HSJM.AI.AppLayer;
using HSJM.AI.AppLayer.Basic.DotnetExecutor;
using HSJM.AI.AppLayer.Basic.DotnetExecutor.DTOS;
using HSJM.AI.Core;
using HSJM.AI.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HSJM.AI.Controllers.Basic.DotnetExecutor
{

    [ApiExplorerSettings(GroupName = "Basic")]
    [Route("DotnetExecutor")]
    [ApiController]
    [ModelValidation]
    [Authorize]
    public class DotnetExecutorControllerr : ControllerBase
    {
        private readonly DotnetExecutorApp _executorApp;
        private readonly ILogger<DotnetExecutorControllerr> _logger;
        public DotnetExecutorControllerr(DotnetExecutorApp executorApp,
        ILogger<DotnetExecutorControllerr> logger)
        {
            _executorApp = executorApp;
            _logger = logger;
        }

        /// <summary>
        /// 执行.NET代码
        /// </summary>
        /// <param name="mode">执行模式: singlefile 或 project</param>
        /// <param name="filePath">单文件路径（当mode=singlefile时使用）</param>
        /// <param name="projectPath">项目路径（当mode=project时使用）</param>
        /// <param name="parameters">执行参数（每行一个）</param>
        /// <param name="fileType">文件类型</param>
        /// <param name="files">上传的文件</param>
        [HttpPost("run-dotnet")]
        [RequestSizeLimit(52428800)] // 50MB
        [Consumes("multipart/form-data")]
        public async Task<ActionResult> RunDotnet(
            [FromForm] string mode = "singlefile",
            [FromForm] string? filePath = null,
            [FromForm] string? projectPath = null,
            [FromForm] string? parameters = null,
            [FromForm] string? fileType = null, [FromForm] List<IFormFile>? files = null)
        {
            try
            {
                var request = new DotnetExecutionConditionDTO
                {
                    Mode = mode,
                    FilePath = filePath,
                    ProjectPath = projectPath,
                    Parameters = parameters,
                    FileType = fileType,
                    Files = files
                };

                var result = await _executorApp.ExecuteAsync(request);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理请求时发生错误");
                return Ok(ex.Message);
            }
        }

    }
}
