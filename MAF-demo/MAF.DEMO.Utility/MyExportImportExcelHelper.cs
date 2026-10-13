using Magicodes.ExporterAndImporter.Core;
using Magicodes.ExporterAndImporter.Excel;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.NPOIHelper
{
    public class ExportImportResultModel<T>
    {
        public List<T> Data { get; set; }
        public bool HasError { get; set; }

        public string ErrorMessage { get; set; }
    }

    public class ExportExcel<T>
    {
        public List<T> Data { get; set; }
        public string FileName { get; set; }

        public string FilePath { get; set; }

        public string TemplateFileName { get; set; }
    }

    public class ExportExcelByTemplateCondition<T>
    {
        public T Data { get; set; }
        public string FileName { get; set; }

        public string FilePath { get; set; }

        public string TemplateFileName { get; set; }
    }
    public static class MyExportImportExcelHelper
    {
        static IExcelImporter importer = new ExcelImporter();

        /// <summary>
        /// 读取excel ，只支持xlsx
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static async Task<ExportImportResultModel<T>> ImportExcelAsync<T>(Stream stream) where T : class, new()
        {
            ExportImportResultModel<T> exportImportResultModel = new ExportImportResultModel<T>();
            var importResult = await importer.Import<T>(stream);

            if (importResult.HasError)
            {
                StringBuilder errorMessage = new StringBuilder();
                importResult.RowErrors.OrderBy(t => t.RowIndex).ToList().ForEach(t =>
                {
                    errorMessage.Append($"第{t.RowIndex}行错误：");
                    t.FieldErrors.ToList().ForEach(error =>
                    {
                        errorMessage.Append($"{error.Key}:{error.Value}");
                    });
                });
                exportImportResultModel.ErrorMessage = errorMessage.ToString();
            }
            exportImportResultModel.Data = importResult.Data?.ToList();
            exportImportResultModel.HasError = importResult.HasError;
            return exportImportResultModel;
        }

        static IExporter exporter = new ExcelExporter();

        public static async Task<ExportImportResultModel<string>> ExportExcelAsync<T>(ExportExcel<T> condition) where T : class, new()
        {

            var tplPath = condition.TemplateFileName;
            var fileName = condition.FileName;
            var filePath = condition.FilePath;
            var data = condition.Data;
            if (!Directory.Exists(filePath))
            {
                Directory.CreateDirectory(filePath);
            }
            if (File.Exists(filePath + fileName))
            {
                File.Delete(filePath + fileName);
            }
            ExportImportResultModel<string> exportImportResultModel = new ExportImportResultModel<string>();
            //根据模板导出
            try
            {

                var result = await exporter.Export<T>(filePath + fileName, data);
            }
            catch (Exception ex)
            {
                exportImportResultModel.HasError = true;
                exportImportResultModel.ErrorMessage = ex.ToString();
                return exportImportResultModel;
            }
            exportImportResultModel.HasError = false;
            return exportImportResultModel;
        }
        static IExportFileByTemplate exporterTemplate = new ExcelExporter();
        public static async Task<ExportImportResultModel<string>> ExportExcelByTemplateAsync<T>(ExportExcelByTemplateCondition<T> condition) where T : class, new()
        {

            var tplPath = condition.TemplateFileName;
            var fileName = condition.FileName;
            var filePath = condition.FilePath;
            var data = condition.Data;
            if (!Directory.Exists(filePath))
            {
                Directory.CreateDirectory(filePath);
            }
            if (File.Exists(filePath + fileName))
            {
                File.Delete(filePath + fileName);
            }
            ExportImportResultModel<string> exportImportResultModel = new ExportImportResultModel<string>();
            //根据模板导出
            try
            {

                var result = await exporterTemplate.ExportByTemplate<T>(filePath + fileName, data, tplPath);

            }
            catch (Exception ex)
            {
                exportImportResultModel.HasError = true;
                exportImportResultModel.ErrorMessage = ex.ToString();
                return exportImportResultModel;
            }
            exportImportResultModel.HasError = false;
            return exportImportResultModel;
        }

   
    }
}
