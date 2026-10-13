using HSJM.AI.AppLayer.PLC.PLC_Upload;
using Xunit;

namespace HSJM.AI.IgwRegistration.Tests;

/// <summary>
/// 上传包文件分类（2026-09-21 收窄）。原先放行 `.json`，但解析器只认 `.scl`/`.awl` 与 XML，
/// `.json` 走 XML 分支必然识别失败——导出包里的 manifest.json / udt_index.json 因此
/// 各留一行 ParseStatus=1 的孤儿 PLC_SourceFileEntity；manifest.json 内容每次都变，
/// 哈希跳过永不命中，每上传一次多一行、从不回收。
/// </summary>
public class PlcUploadFileClassifierTests
{
    [Theory]
    [InlineData("FB320_OP30Manual.xml")]
    [InlineData("UDT_General.ml")]
    [InlineData("iotags.csv")]
    [InlineData("BLOCK.XML")]            // 扩展名大小写不敏感
    public void Plc_source_files_are_accepted(string fileName) =>
        Assert.True(PLC_UploadFileClassifier.IsGeneralPlcSource(fileName));

    [Theory]
    [InlineData("manifest.json")]        // 导出包元数据，非 PLC 源
    [InlineData("udt_index.json")]       // 同上
    [InlineData("anything.JSON")]
    [InlineData("block_path.txt")]
    [InlineData("readme.md")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_plc_files_are_rejected(string fileName) =>
        Assert.False(PLC_UploadFileClassifier.IsGeneralPlcSource(fileName));
}
