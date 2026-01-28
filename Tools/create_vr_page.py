import os

path = r"C:\Users\Redacted\Documents\latticeveil.github.io\vr\index.html"
html = """<!DOCTYPE html>
<html>
  <head>
    <meta charset="utf-8">
    <title>LatticeVeil VR | WebXR Room</title>
    <script src="https://aframe.io/releases/1.4.2/aframe.min.js"></script>
    <script src="https://unpkg.com/aframe-environment-component@1.3.2/dist/aframe-environment-component.min.js"></script>
  </head>
  <body>
    <a-scene>
      <a-entity environment="preset: pixel; seed: 42; dressingAmount: 50; grid: dots; groundColor: #222; skyColor: #111; horizonColor: #333;"></a-entity>
      <a-assets><img id="logo" src="../assets/img/logo.png"></a-assets>
      <a-entity id="rig" position="0 0 4">
        <a-camera look-controls wasd-controls><a-cursor color="#4e9a06" fuse="false"></a-cursor></a-camera>
        <a-entity oculus-touch-controls="hand: left"></a-entity>
        <a-entity oculus-touch-controls="hand: right"></a-entity>
      </a-entity>
      <a-image src="#logo" position="0 2.5 -2" width="2" height="0.5" transparent="true"></a-image>
      <a-entity position="0 1.5 -2">
        <a-plane color="#2d2d2d" width="3" height="2" position="0 0 -0.05"></a-plane>
        <a-text value="LATTICEVEIL VR PORTAL" align="center" position="0 0.7 0" color="#e9b96e" width="4"></a-text>
        <a-entity class="clickable" position="-0.8 -0.2 0" geometry="primitive: plane; width: 0.7; height: 0.4" material="color: #4e9a06" onclick="window.location.href='../'">
          <a-text value="HOME" align="center" position="0 0 0.01" color="#fff" width="2"></a-text>
        </a-entity>
        <a-entity class="clickable" position="0 -0.2 0" geometry="primitive: plane; width: 0.7; height: 0.4" material="color: #4e9a06" onclick="window.location.href='../#coding'">
          <a-text value="DEVLOG" align="center" position="0 0 0.01" color="#fff" width="2"></a-text>
        </a-entity>
        <a-entity class="clickable" position="0.8 -0.2 0" geometry="primitive: plane; width: 0.7; height: 0.4" material="color: #4e9a06" onclick="window.location.href='../#assets'">
          <a-text value="BLOCKS" align="center" position="0 0 0.01" color="#fff" width="2"></a-text>
        </a-entity>
      </a-entity>
      <a-box position="-2 0.5 -3" color="#4e9a06" animation="property: rotation; to: 0 360 0; loop: true; dur: 10000"></a-box>
      <a-box position="2 0.5 -3" color="#e9b96e" animation="property: rotation; to: 0 -360 0; loop: true; dur: 8000"></a-box>
    </a-scene>
    <script>
      document.querySelectorAll('.clickable').forEach(el => {
        el.addEventListener('mouseenter', () => el.setAttribute('material', 'color', '#3a7304'));
        el.addEventListener('mouseleave', () => el.setAttribute('material', 'color', '#4e9a06'));
      });
    </script>
  </body>
</html>
"""

os.makedirs(os.path.dirname(path), exist_ok=True)
with open(path, 'w', encoding='utf-8') as f:
    f.write(html)

print(f"Created {path}")
