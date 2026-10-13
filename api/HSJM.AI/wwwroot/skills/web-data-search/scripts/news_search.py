import requests
from bs4 import BeautifulSoup
import json
from datetime import datetime

def search_tech_news():
    """搜索最新的科技新闻"""
    try:
        # 使用DuckDuckGo进行搜索
        search_url = "https://html.duckduckgo.com/html/"
        headers = {
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36'
        }
        params = {'q': '最新科技新闻'}
        
        response = requests.get(search_url, headers=headers, params=params, timeout=10)
        soup = BeautifulSoup(response.content, 'html.parser')
        
        news_items = []
        results = soup.find_all('div', class_='result')
        
        for i, result in enumerate(results[:5]):  # 获取前5条结果
            title_elem = result.find('a', class_='result__a')
            link_elem = result.find('a', class_='result__a')
            snippet_elem = result.find('a', class_='result__snippet')
            
            if title_elem and link_elem:
                title = title_elem.get_text().strip()
                link = link_elem.get('href')
                snippet = snippet_elem.get_text().strip() if snippet_elem else ""
                
                news_items.append({
                    'title': title,
                    'link': link,
                    'snippet': snippet,
                    'timestamp': datetime.now().isoformat()
                })
        
        return {
            'success': True,
            'news': news_items,
            'total': len(news_items)
        }
        
    except Exception as e:
        return {
            'success': False,
            'error': str(e)
        }

if __name__ == "__main__":
    result = search_tech_news()
    print(json.dumps(result, ensure_ascii=False, indent=2))