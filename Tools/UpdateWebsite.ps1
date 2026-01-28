$path = "C:\Users\Redacted\Documents\latticeveil.github.io\index.html"
$content = Get-Content $path -Raw

# 1. Add Touch Events
$newJs = @"
            // Unified Input Handling
            const onStart = (x, y) => { isDragging = true; prevMouse = { x, y }; };
            const onEnd = () => { isDragging = false; };
            const onMove = (x, y) => {
                if (!isDragging) return;
                const delta = { x: x - prevMouse.x, y: y - prevMouse.y };
                cube.rotation.y += delta.x * 0.01;
                cube.rotation.x += delta.y * 0.01;
                prevMouse = { x, y };
            };

            // Mouse
            container.onmousedown = (e) => onStart(e.clientX, e.clientY);
            window.onmouseup = onEnd;
            window.onmousemove = (e) => onMove(e.clientX, e.clientY);

            // Touch (Mobile Fix)
            container.ontouchstart = (e) => {
                // Prevent scrolling when rotating model
                if (e.target === renderer.domElement) e.preventDefault();
                onStart(e.touches[0].clientX, e.touches[0].clientY);
            };
            window.ontouchend = onEnd;
            window.ontouchmove = (e) => {
                onMove(e.touches[0].clientX, e.touches[0].clientY);
            };
"@

$oldBlock = @"
            // Interaction
            let isDragging = false;
            let prevMouse = { x: 0, y: 0 };
            container.onmousedown = (e) => { isDragging = true; prevMouse = { x: e.clientX, y: e.clientY }; };
            window.onmouseup = () => { isDragging = false; };
            window.onmousemove = (e) => {
                if (!isDragging) return;
                const delta = { x: e.clientX - prevMouse.x, y: e.clientY - prevMouse.y };
                cube.rotation.y += delta.x * 0.01;
                cube.rotation.x += delta.y * 0.01;
                prevMouse = { x: e.clientX, y: e.clientY };
            };
"@

# Normalize line endings for replacement
$content = $content.Replace("`r`n", "`n")
$oldBlock = $oldBlock.Replace("`r`n", "`n")
$newJs = $newJs.Replace("`r`n", "`n")

if ($content.Contains($oldBlock)) {
    $content = $content.Replace($oldBlock, $newJs)
    Write-Host "Updated JS for touch support."
} else {
    Write-Host "WARNING: Could not find old JS block to replace."
}

# 2. Add Dev Log
$logMarker = '<div id="log-game" class="sub-content active">'
$newLog = @"
                <div class="log-entry">
                    <div class="log-header">
                        <h3>🔧 Restoring Order: EOS Fixes & Repo Cleanup</h3>
                        <span class="log-date">JAN 12, 2026</span>
                    </div>
                    <div class="log-body">
                        <p>We've just pushed a major update to the <strong>LatticeVeil</strong> repository infrastructure and game configuration.</p>
                        <ul>
                            <li><strong>EOS Service Update:</strong> Strictly using <code>eos-service.onrender.com</code>.</li>
                            <li><strong>Robust Connectivity:</strong> Added automatic credential fallback if remote fails.</li>
                            <li><strong>Repo Cleanup:</strong> Removed sensitive SDK tools; runtime DLLs preserved.</li>
                            <li><strong>Easier Access:</strong> Executable and Source Zip are now in the root.</li>
                        </ul>
                        <p>Check out the latest files on the <a href="https://github.com/latticeveil/" style="color: var(--primary);">GitHub Repository</a>.</p>
                    </div>
                </div>
"@

# Check if log is already there to avoid dupes
if (-not $content.Contains("Restoring Order: EOS Fixes")) {
    $content = $content.Replace($logMarker, "$logMarker`n$newLog")
    Write-Host "Added Dev Log."
} else {
    Write-Host "Dev log already present."
}

Set-Content -Path $path -Value $content -Encoding UTF8
Write-Host "Website index.html write complete."
