import json
import os
from pathlib import Path
from urllib.request import urlopen


root = Path(__file__).resolve().parents[1]
service = json.loads((root / "tools" / "service.json").read_text(encoding="utf-8"))
url = os.environ.get("OPENAPI_URL", service["default_openapi_url"])
with urlopen(url, timeout=30) as response:
    api = json.load(response)

assert api["info"]["title"] == service["title"], "Incorrect API title"
assert api["info"]["version"] == service["version"], "Incorrect API version"
assert api["info"].get("license", {}).get("name") == "MIT", "Incorrect API license"
actual_tags = {tag["name"]: tag.get("description", "") for tag in api.get("tags", [])}
assert actual_tags == service["tags"], "Incorrect API tags"

actual_operations = {
    (method.upper(), path): operation
    for path, item in api["paths"].items()
    for method, operation in item.items()
    if method.lower() in {"get", "post", "put", "patch", "delete", "head", "options", "trace"}
}
expected_operations = {
    (item["method"], item["path"]): item for item in service["operations"]
}
assert actual_operations.keys() == expected_operations.keys(), "Undocumented or missing API operations"

for key, definition in expected_operations.items():
    operation = actual_operations[key]
    assert operation.get("tags") == [definition["tag"]], f"Incorrect tag for {key}"
    assert operation.get("summary") == definition["summary"], f"Incorrect summary for {key}"
    assert operation.get("description") == definition["description"], f"Incorrect description for {key}"
    assert operation.get("security") == [{definition["security"]: []}], f"Incorrect security for {key}"
    assert str(definition["success"]) in operation.get("responses", {}), f"Missing success response for {key}"

schemes = api.get("components", {}).get("securitySchemes", {})
assert set(service["security_schemes"]).issubset(schemes), "Missing shared security schemes"
print(f'{service["title"]}: v1, {len(actual_tags)} groups, {len(actual_operations)} operations verified')
