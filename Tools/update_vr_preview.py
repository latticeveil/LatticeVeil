import os

path = r"C:\Users\Redacted\Documents\latticeveil.github.io\vr\index.html"

if not os.path.exists(path):
    print(f"File not found: {path}")
    exit(1)

with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Add Preview UI style
style_marker = "</head>"
preview_style = "    <style>
      #preview-ui {
        position: fixed; top: 20px; left: 20px; z-index: 100;
        background: rgba(0,0,0,0.7); color: white; padding: 15px;
        font-family: monospace; border: 2px solid #4e9a06;
      }
    </style>\n"

if "#preview-ui" not in content:
    content = content.replace(style_marker, preview_style + style_marker)

# Add Preview UI HTML
body_marker = "<body>"
preview_html = "\n    <div id=\"preview-ui\">
      <h2 style=\"margin:0; color:#e9b96e;\">PREVIEW MODE</h2>
      <p>Use WASD + Mouse to explore.<br>Click panels to return to site.<br>Enter VR on Quest for full experience.</p>
    </div>"

if 'id="preview-ui"' not in content:
    content = content.replace(body_marker, body_marker + preview_html)

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)

print("Updated vr/index.html with Preview UI.")