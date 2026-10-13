/**
 * JSON 格式化工具
 * @param data JSON 对象或字符串
 */
export function formatCallData(data: any): string {
    try {
        if (typeof data === 'string') {
            data = JSON.parse(data);
        }
        return JSON.stringify(data, null, 2);
    } catch (e) {
        return String(data);
    }
}

/**
 * Unicode 解码工具，用于将 \uXXXX 转换为中文
 * @param str 可能包含 Unicode 转义序列的字符串
 */
export function decodeUnicode(str: string): string {
    return str.replace(/\\u[\dA-F]{4}/gi, (match) => {
        return String.fromCharCode(parseInt(match.replace(/\\u/g, ''), 16));
    });
}
