import sys
import json
import argparse
import warnings
# 忽略 duckduckgo_search 的重命名警告，以免污染 Stderr 导致误判
warnings.filterwarnings("ignore", category=RuntimeWarning, module="duckduckgo_search")

from duckduckgo_search import DDGS

def main():
    # 1. 解析参数
    parser = argparse.ArgumentParser(description='Web Search Skill using DuckDuckGo')
    parser.add_argument('--input-base64', type=str, required=True, help='Base64 encoded JSON input')
    args = parser.parse_args()

    # 2. 解码输入
    try:
        import base64
        input_json = base64.b64decode(args.input_base64).decode('utf-8')
        params = json.loads(input_json)
        
        query = params.get('query')
        max_results = params.get('max_results', 5)
        region = params.get('region', "wt-wt")

        if not query:
            raise ValueError("Missing required parameter: query")

    except Exception as e:
        print(json.dumps({
            "success": False,
            "error": f"Input parsing error: {str(e)}"
        }))
        sys.exit(1)

    # 3. 执行搜索
    try:
        results = []
        with DDGS() as ddgs:
            # text() 方法执行文本搜索
            search_gen = ddgs.text(
                keywords=query,
                region=region,
                safesearch='moderate',
                timelimit=None,
                max_results=max_results
            )
            
            for r in search_gen:
                results.append({
                    "title": r.get('title'),
                    "href": r.get('href'),
                    "body": r.get('body')
                })

        # 4. 输出结果
        output = {
            "success": True,
            "query": query,
            "count": len(results),
            "results": results
        }
        print(json.dumps(output, ensure_ascii=False))

    except Exception as e:
        print(json.dumps({
            "success": False,
            "error": f"Search execution error: {str(e)}"
        }))
        sys.exit(1)

if __name__ == "__main__":
    main()
