# Airo Stream Server

**Airo Stream** is a low-latency 1080p 60fps wireless second display server via WebRTC, designed primarily for local network use. 

This application allows you to seamlessly extend your Windows desktop to other devices (like a laptop, iPad, or TV browser) over your local Wi-Fi without needing a physical HDMI dummy plug. It programmatically creates a virtual display and pipes it through a hardware-accelerated WebRTC stream directly to your browser.

Because this application interacts with low-level Windows APIs for display capturing and high-performance video encoding, there are a few strict requirements you must satisfy before running it.

## System Requirements
- **OS Version:** Windows 10 (Version 2004 or newer) or Windows 11.
- **.NET Runtime:** .NET 8.0 Desktop Runtime is required to run the server. [Download here](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).

## Required Dependencies

### 1. Configuration File (config.json)
Airo Stream relies on a `config.json` file to load WebRTC ICE servers.
1. In the source folder, copy the `config.example.json` file and rename it to `config.json`.
2. By default, this file uses a safe Google STUN server. If you run your own TURN server for over-the-internet connections, you can add those credentials here without worrying about accidentally committing them.

### 2. FFmpeg (Video Encoding)
Airo Stream uses **FFmpeg** to encode the display capture into a high-performance H.264 video stream. **FFmpeg is not bundled with this application.**
- **Without FFmpeg**, WebRTC will connect successfully, but you will only see a **black screen**. 
- The application will alert you on startup if FFmpeg is missing.

**Installation Steps:**
1. Download a Windows build of FFmpeg (e.g., from [BtbN/FFmpeg-Builds on GitHub](https://github.com/BtbN/FFmpeg-Builds/releases) or `gyan.dev`).
2. Extract the archive.
3. Place `ffmpeg.exe` in `C:\ffmpeg\bin\` OR add the `bin` folder containing `ffmpeg.exe` to your system `PATH` environment variable.

### 3. Amyuni Virtual Display Driver (usbmmidd)
Airo Stream relies on the **Amyuni USB Mobile Monitor Virtual Display** driver (`usbmmidd`) to create the virtual monitor that gets streamed. 
- Due to licensing restrictions, this driver is **not bundled** directly in the source repository.
- You must manually download it from Amyuni and place it in the application's `Driver` folder.

**Installation Steps:**
1. Download the `usbmmidd_v2` package directly from Amyuni.
2. Extract the files and create a `Driver` folder next to your built `AiroWebRTCServer.exe`.
3. **Extracting Note:** Sometimes the zip file extracts into an extra subfolder (e.g., `usbmmidd_v2\usbmmidd_v2`). Make sure you move the files up so that `deviceinstaller64.exe` is sitting directly inside the `Driver\` folder.

> **Note on Administrator Privileges:** The application automatically runs as an Administrator because installing or enabling the virtual display driver requires elevated permissions. You will see a UAC (User Account Control) prompt every time you launch the app.

---

## Known Limitations
- **Windows Only:** This server only runs on Windows (due to Desktop Duplication API and Windows-specific driver commands).
- **Local Network Only:** The WebRTC connection relies on direct local IP resolution. Modern browsers attempt to hide local IPs using mDNS (`.local` addresses), but Airo Stream intercepts this and maps the connection back to the IP you explicitly typed/scanned to load the page. Because of this, it only works locally and does not work over the internet natively without setting up a TURN server or VPN (like Tailscale/ZeroTier).
- **Hardcoded Port:** The server strictly runs on **port 5000**. If another service (like Docker or another web framework) is using port 5000, Airo Stream will prompt you to close the conflicting service and retry.

---

## Troubleshooting First-Run Failures

### 1. WebRTC connects, but the screen is completely black.
**Cause:** FFmpeg is missing from your system, or the path is not correctly configured. The server captured the video stream but had no encoder to compress it. 
**Fix:** See the FFmpeg installation steps above. Ensure `ffmpeg.exe` can be run from your command prompt.

### 2. "Port 5000 is already in use" Error Dialog.
**Cause:** Another application is occupying the HTTP/WebSocket port.
**Fix:** You must free up port 5000. Open a command prompt and run `netstat -ano | findstr :5000` to find the PID of the conflicting process, then kill it via Task Manager.

### 3. Virtual display is not created (Client shows your primary monitor instead).
**Cause:** The application failed to install or enable the virtual display driver.
**Fix:**
- Ensure you placed the Amyuni driver files inside the `Driver` directory next to the executable.
- Ensure you accepted the Administrator UAC prompt.
- Manually run `deviceinstaller64.exe install usbmmIdd.inf usbmmidd` from an Administrator command prompt to force-install the base driver.

### 4. Only 1 monitor detected / Resolution is wrong.
**Cause:** Windows sometimes defaults the virtual display to a weird resolution or mirrors the displays instead of extending them.
**Fix:** Open Windows **Display Settings** and ensure the displays are set to **"Extend these displays"**. Click the virtual display and manually set its resolution to `1920x1080`.

### 5. Video is severely choppy despite no error messages.
**Cause:** You may have a weak integrated graphics chip (iGPU) that passed the detection but is struggling to sustain a 1080p60 encode.
**Fix:** You can force the application to use the reliable (but CPU-heavy) software encoder. Run the application from a command prompt where you've set the environment variable:
`set AIRO_FORCE_SOFTWARE_ENCODER=1`
`Airo.exe`

---

## Architecture & Code Map
For developers wanting to explore the codebase:
- **`Program.cs`:** The core engine. It contains the HTTP server, the WebSocket server (`SignalingBehavior`), the WebRTC peer connection logic (`StartVideo()`), and the hardware `EncoderDetector` which runs the FFmpeg dry-run to select `h264_nvenc`/`amf`/`qsv`/`libx264`.
- **`public/app.js`:** The client-side WebRTC logic, handling exponential backoff reconnects, ICE gathering, and displaying the video stream.
