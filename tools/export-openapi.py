import json
import os
from pathlib import Path
from urllib.request import urlopen


root = Path(__file__).resolve().parents[1]
service = json.loads((root / "tools" / "service.json").read_text(encoding="utf-8"))
url = os.environ.get("OPENAPI_URL", service["default_openapi_url"])

with urlopen(url, timeout=30) as response:
    document = json.load(response)

(root / "docs" / "openapi.json").write_text(
    json.dumps(document, indent=2, ensure_ascii=False) + "\n",
    encoding="utf-8",
)
print(f'Exported {service["title"]} from {url} to docs/openapi.json')
