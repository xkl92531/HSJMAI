import sys
import json
import os
import argparse
import traceback

# Try importing engines
HAS_PLAYWRIGHT = False
try:
    from playwright.sync_api import sync_playwright
    HAS_PLAYWRIGHT = True
except ImportError:
    pass

HAS_REQUESTS = False
try:
    import requests
    from bs4 import BeautifulSoup
    HAS_REQUESTS = True
except ImportError:
    pass

def run_playwright(url, action, selector=None):
    result = {"success": False, "title": "", "content": "", "screenshot_path": "", "error": ""}
    
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True)
        context = browser.new_context(
            user_agent="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36"
        )
        page = context.new_page()
        
        try:
            page.goto(url, wait_until='networkidle', timeout=30000)
        except Exception:
            page.goto(url, wait_until='domcontentloaded', timeout=30000)

        result["title"] = page.title()

        if action == "screenshot":
            screenshot_dir = os.path.join(os.path.dirname(__file__), "screenshots")
            os.makedirs(screenshot_dir, exist_ok=True)
            filename = f"{os.path.basename(url)[:20].replace(':', '')}_{os.urandom(4).hex()}.png"
            filepath = os.path.join(screenshot_dir, filename)
            page.screenshot(path=filepath, full_page=True)
            result["screenshot_path"] = filepath
            result["content"] = f"Screenshot saved to {filepath}"
            result["success"] = True

        elif action == "get_html":
            if selector:
                try:
                    content = page.inner_html(selector)
                except:
                    content = page.content() # Fallback
            else:
                content = page.content()
            result["content"] = content
            result["success"] = True

        else: # extract_text
            if selector:
                try:
                    text = page.inner_text(selector)
                except:
                    text = page.inner_text("body")
            else:
                text = page.inner_text("body")
            result["content"] = text
            result["success"] = True

        browser.close()
    return result

def run_requests(url, action, selector=None):
    result = {"success": False, "title": "", "content": "", "screenshot_path": "", "error": ""}
    
    try:
        headers = {
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36"
        }
        resp = requests.get(url, headers=headers, timeout=30)
        resp.raise_for_status()
        
        soup = BeautifulSoup(resp.text, 'html.parser')
        result["title"] = soup.title.string if soup.title else ""
        
        if action == "screenshot":
            result["error"] = "Screenshot not supported in 'requests' mode (Playwright required)."
            result["success"] = False
            
        elif action == "get_html":
            if selector:
                selected = soup.select_one(selector)
                result["content"] = str(selected) if selected else str(soup)
            else:
                result["content"] = str(soup)
            result["success"] = True
            
        else: # extract_text
            # Remove scripts and styles
            for script in soup(["script", "style"]):
                script.decompose()
                
            if selector:
                selected = soup.select_one(selector)
                text = selected.get_text(separator='\n', strip=True) if selected else soup.get_text(separator='\n', strip=True)
            else:
                text = soup.get_text(separator='\n', strip=True)
                
            result["content"] = text
            result["success"] = True
            
    except Exception as e:
        result["error"] = str(e)
        result["success"] = False
        
    return result

def run_browser(url, action="extract_text", selector=None):
    # Decision logic: output JSON strictly
    
    # 1. Try Playwright First (if available and not explicitly disabled?)
    if HAS_PLAYWRIGHT:
        try:
            return run_playwright(url, action, selector)
        except Exception as e:
            # If Playwright fails (e.g. browser not found), propagate error only if requests is also missing
            if not HAS_REQUESTS:
               return {"success": False, "error": f"Playwright failed: {e}. Requests module not found."}
            # Else fall through to requests
            pass
            
    # 2. Fallback to Requests
    if HAS_REQUESTS:
        return run_requests(url, action, selector)
    
    return {"success": False, "error": "No suitable browser engine found (install 'playwright' or 'requests')."}

if __name__ == "__main__":
    # Ensure stdout encoding is utf-8 for JSON
    sys.stdout.reconfigure(encoding='utf-8')
    
    # Argparse to support both legacy positional arg and new --input-base64
    parser = argparse.ArgumentParser(description="Web Browser Skill")
    parser.add_argument("input_json", nargs="?", help="JSON string or file path")
    parser.add_argument("--input-base64", help="Base64 encoded JSON string")
    
    args_parsed = parser.parse_args()
    
    input_str = "{}"
    
    if args_parsed.input_base64:
        import base64
        try:
            # Decode base64 input
            input_bytes = base64.b64decode(args_parsed.input_base64)
            input_str = input_bytes.decode('utf-8')
        except Exception as e:
            print(json.dumps({"success": False, "error": f"Failed to decode base64 input: {e}"}, ensure_ascii=False))
            sys.exit(1)
            
    elif args_parsed.input_json:
        input_str = args_parsed.input_json
        # Support reading from file
        if os.path.isfile(input_str):
            try:
                with open(input_str, "r", encoding="utf-8") as f:
                    input_str = f.read()
            except:
                pass
    
    try:
        args_data = json.loads(input_str)
    except:
        # Fallback for simple URL string
        if input_str.startswith("http"):
             args_data = {"url": input_str}
        else:
             args_data = {}

    url = args_data.get("url")
    if not url:
        print(json.dumps({"success": False, "error": "URL is required"}, ensure_ascii=False))
        sys.exit(1)

    action = args_data.get("action", "extract_text")
    selector = args_data.get("selector")

    output = run_browser(url, action, selector)
    print(json.dumps(output, ensure_ascii=False))
