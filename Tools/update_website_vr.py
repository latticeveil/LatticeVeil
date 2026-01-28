import os

path = r"C:\Users\Redacted\Documents\latticeveil.github.io\index.html"

with open(path, 'rb') as f:
    data = f.read()

# 1. Add VR Button Style
css_marker = b'box-shadow: 4px 4px 0px #000; }'
vr_style = b"""
        #vrIcon {
            display: none;
            position: fixed;
            bottom: 20px;
            right: 20px;
            z-index: 1000;
            background: #4e9a06;
            color: white;
            border: 4px solid white;
            padding: 15px;
            border-radius: 50%;
            cursor: pointer;
            box-shadow: 0 0 15px rgba(78, 154, 6, 0.5);
            transition: transform 0.2s;
        }
        #vrIcon:hover { transform: scale(1.1); }
        #vrIcon i { font-size: 24px; }
"""
if b'#vrIcon' not in data:
    data = data.replace(css_marker, css_marker + vr_style)

# 2. Add VR Button HTML
body_marker = b'<body>'
vr_html = b"""
    <button id="vrIcon" onclick="window.location.href='./vr/'" title="Enter VR (Quest Only)">
        <i class="fas fa-vr-cardboard"></i>
    </button>
"""
if b'id="vrIcon"' not in data:
    data = data.replace(body_marker, body_marker + vr_html)

# 3. Add Detection Logic and Hash-Tab Sync
script_marker = b'populateGalleries();'
detection_js = b"""
        // WebXR / Quest Detection
        async function checkVR() {
            const isQuest = /OculusBrowser|Quest/i.test(navigator.userAgent);
            const vrSupported = navigator.xr && await navigator.xr.isSessionSupported('immersive-vr');
            if (isQuest && vrSupported) {
                document.getElementById('vrIcon').style.display = 'block';
            }
        }
        checkVR();

        // Sync Tabs with URL Hash
        function syncTab() {
            const hash = window.location.hash.replace('#', '');
            if (hash && document.getElementById(hash)) {
                const btn = Array.from(document.querySelectorAll('.tab-link')).find(l => 
                    l.textContent.toLowerCase().includes(hash.toLowerCase()) || 
                    (hash === 'coding' && l.textContent.includes('DEV LOG')) ||
                    (hash === 'assets' && l.textContent.includes('TEXTURES'))
                );
                if (btn) btn.click();
            }
        }
        window.addEventListener('hashchange', syncTab);
        syncTab();
"""
if b'checkVR();' not in data:
    data = data.replace(script_marker, script_marker + detection_js)

# 4. Add Devlog Entry
log_marker = b'<div id="log-website" class="sub-content">'
new_log = """
                <div class="log-entry">
                    <div class="log-header">
                        <h3>🥽 New Feature: WebXR VR Portal</h3>
                        <span class="log-date">JAN 12, 2026</span>
                    </div>
                    <div class="log-body">
                        <p>Meta Quest users rejoice! We've launched an experimental <strong>WebXR VR Portal</strong>. If you are browsing on a Quest 2 or 3, look for the VR icon in the bottom corner to enter an immersive voxel room.</p>
                        <p>The VR room features a central kiosk where you can jump directly back to specific site sections while remaining in your headset.</p>
                    </div>
                </div>"""#.encode('utf-8')

if b'WebXR VR Portal' not in data:
    data = data.replace(log_marker, log_marker + b'\n' + new_log.encode('utf-8'))

with open(path, 'wb') as f:
    f.write(data)

print("Updated index.html with VR button, detection, and devlog.")
