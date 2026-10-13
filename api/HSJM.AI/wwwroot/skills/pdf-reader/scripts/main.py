import sys
import json
import pdfplumber

def extract_text_from_pdf(pdf_path):
    """从PDF文件中提取文本内容 (使用 pdfplumber)"""
    text_content = ""
    try:
        with pdfplumber.open(pdf_path) as pdf:
            for page in pdf.pages:
                # 尝试提取文本，如果提取失败则忽略该页或添加换行
                page_text = page.extract_text()
                if page_text:
                    text_content += page_text + "\n"
        
        return text_content.strip()
    except Exception as e:
        raise Exception(f"PDF解析失败: {str(e)}")

def main():
    try:
        # 1. 健壮的输入读取 (抗 BOM)
        # 读取所有 stdin 内容
        input_data = sys.stdin.read().strip()
        # 去除 BOM
        input_data = input_data.lstrip('\ufeff')
        
        sys.stderr.write(f"Debug: Raw Input: {repr(input_data)}\n")

        if not input_data:
            print(json.dumps({"error": "没有提供输入数据"}))
            sys.exit(1)
        
        input_json = json.loads(input_data)
        pdf_path = input_json.get("pdfPath")
        
        if not pdf_path:
            print(json.dumps({"error": "缺少必要的参数: pdfPath"}))
            sys.exit(1)
        
        sys.stderr.write(f"Debug: Opening PDF: {pdf_path}\n")

        # 提取PDF文本
        text_content = extract_text_from_pdf(pdf_path)
        
        # 返回结果
        result = {
            "textContent": text_content
        }
        
        print(json.dumps(result, ensure_ascii=False))
        
    except json.JSONDecodeError:
        error_msg = "输入数据不是有效的JSON格式"
        sys.stderr.write(f"Error: {error_msg}\n")
        print(json.dumps({"error": error_msg}, ensure_ascii=False))
        sys.exit(1)
    except Exception as e:
        sys.stderr.write(f"Error: {str(e)}\n")
        print(json.dumps({"error": str(e)}, ensure_ascii=False))
        sys.exit(1)

if __name__ == "__main__":
    main()