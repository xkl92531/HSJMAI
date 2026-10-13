import sys
import json
import io
import contextlib
import traceback
import os
import subprocess

def execute_code(code):
    result = {
        "success": False,
        "stdout": "",
        "stderr": "",
        "result_files": []
    }

    # Capture stdout/stderr
    stdout_capture = io.StringIO()
    stderr_capture = io.StringIO()

    try:
        # Create a temporary directory for file outputs if needed, or use current
        # Ideally we should run this in a restricted environment, but for now we run as subprocess
        
        # We can write the code to a file and run it with python to ensure isolation from the main process
        # This prevents `sys.exit()` in user code from killing the agent
        
        temp_script = f"temp_script_{os.urandom(4).hex()}.py"
        with open(temp_script, "w", encoding="utf-8") as f:
            f.write(code)
            
        # Run subprocess
        # Timeout after 60 seconds
        proc = subprocess.run(
            [sys.executable, temp_script],
            capture_output=True,
            text=True,
            timeout=60
        )
        
        result["stdout"] = proc.stdout
        result["stderr"] = proc.stderr
        
        if proc.returncode == 0:
            result["success"] = True
        else:
            result["success"] = False
            result["error"] = f"Process exited with code {proc.returncode}"

    except subprocess.TimeoutExpired:
        result["error"] = "Execution timed out (60s limit)"
        result["stderr"] = "TimeoutExpired"
    except Exception as e:
        result["error"] = str(e)
        result["stderr"] = traceback.format_exc()
    finally:
        # Cleanup temp file
        if os.path.exists(temp_script):
            try:
                os.remove(temp_script)
            except:
                pass
    
    return result

if __name__ == "__main__":
    # Ensure stdout encoding is utf-8
    sys.stdout.reconfigure(encoding='utf-8')
    
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("input_json", nargs="?", help="JSON string or file path")
    parser.add_argument("--input-base64", help="Base64 encoded JSON string")
    args_parsed = parser.parse_args()
    
    input_str = "{}"
    
    if args_parsed.input_base64:
        import base64
        try:
            input_bytes = base64.b64decode(args_parsed.input_base64)
            input_str = input_bytes.decode('utf-8')
        except Exception as e:
            print(json.dumps({"success": False, "error": f"Failed to decode base64 input: {e}"}, ensure_ascii=False))
            sys.exit(1)
            
    elif args_parsed.input_json:
        input_str = args_parsed.input_json
        # Support reading from file if it is a valid file path
        if os.path.isfile(input_str):
            try:
                with open(input_str, "r", encoding="utf-8") as f:
                    input_str = f.read()
            except:
                pass

    try:
        args = json.loads(input_str)
    except:
        # Fallback: treat transparently? 
        # But if it was a file path and read successfully, it should be valid JSON.
        # If it wasn't a file, maybe it's raw code? 
        # Let's keep the fallback but be careful.
        args = {"code": input_str}

    code = args.get("code")
    if not code:
        # Try to use raw input as code if JSON parse failed or no 'code' field (backwards compat logic, though risky)
        if args.get("code") is None and input_str.strip():
             code = input_str
        else:
             print(json.dumps({"success": False, "error": "No code provided"}, ensure_ascii=False))
             sys.exit(1)

    output = execute_code(code)
    print(json.dumps(output, ensure_ascii=False))
