# Mate2 / Sola

Sola 的 Mate Engine 桌宠适配、语音与 Codex 联动项目。

稳定基线：Mate Engine X3.3.0-HOTFIX-1，保留原版显示修复、窗口缩放、Sola VRM 与表情。本机程序目录为 `D:\MateEngine-Sola`（指向 `D:\桌宠\MateEngine-Sola`）。

仓库存放可编辑模型、整合源码、构建和恢复脚本。第三方程序、本地语音/聊天模型、密钥、聊天记录及用户设置不进入 Git。

语音整合前的源码标签：`stable-before-voice-20260915`。对应本机已安装 DLL 和设置的独立快照位于 `work/before-voice-20260915`，该目录不上传。

## 语音版

- FunASR 中文识别、Silero VAD 断句、小歪原来的 GPT-SoVITS 音色。
- Mate Engine 本地 LLM 流式回复；关闭原聊天菜单仍可继续对话。
- 可连续对话、在说话时打断、切换输入设备、字幕和音量驱动的平滑口型。
- Chromium 回声消除、噪声抑制；默认关闭麦克风。桌宠退出或失联后自动停麦。
- 读取真实 Codex 任务事件查询进度、播报新完成/失败的任务；“让 Codex …”才启动工作目录中的任务。普通聊天不会执行命令。

启动 `Start-Sola.ps1`，点击角色下方的“语音 / 对话”。`Ctrl + Alt + V` 开关麦克风。“切换设备”轮换系统音频输入；首次需要开麦后才能取得完整设备名称。

例如：“Codex 现在进度怎么样？”、“让 Codex 帮我检查这个项目”。面板底部设置工作目录。由桌宠启动的任务使用 Codex CLI 的 `workspace-write` 沙箱；“停止 Codex 任务”仅停止桌宠自己启动的任务。其他桌面任务在 Codex 中停止。任务状态超过 15 分钟未更新会标为过期，不据此猜测任务是否还在运行。

## 本机依赖

程序安装：`D:\MateEngine-Sola`。`Start-Voice.ps1` 复用以下已有依赖，可按实际安装位置调整脚本中的 `$python` 与 `$legacy`：

- Python：`D:\conda\envs\my-neuro\python.exe`；桥接依赖列于 `voice/requirements.txt`。
- 小歪：`D:\deskmate\deskmate\Windomate-codex-bridge`；其 `full-hub/asr_api.py` 与 `full-hub/tts-hub/GPT-SoVITS-Bundle` 保留原本模型和音色。
- 音频运行时：运行 `scripts/Install-AudioRuntime.ps1` 安装固定版本 Electron 44.3.0，核对上游 SHA256，独立放在 D 盘。未安装时可临时使用旧桌宠的运行时。音频直接解码 PCM WAV，避免旧运行时的解码器崩溃。
- Codex CLI 需已安装并登录；当前本地对话使用 Mate Engine 已有模型，无新增云端密钥。

桥接服务只监听 `127.0.0.1:18768`，使用随机本机令牌。默认 ASR / TTS 为 `127.0.0.1:1000` / `127.0.0.1:5000`。私人设置、令牌和日志在 `userdata/voice`；临时朗读音频 10 分钟后清理，麦克风录音不保存到磁盘。`auto_announce: false` 可关闭任务自动播报。退出桌宠会关闭麦克风；共享的 ASR / TTS 模型服务继续驻留，避免影响旧桌宠。`scripts/Stop-Voice.ps1 -Models` 可在不需要两套桌宠时一并停止本次启动的模型服务。

## 存档和回档

GitHub：<https://github.com/rookie-dream343/mate2>。标签 `stable-before-voice-20260915` 为语音整合前版本，`voice-v1-20260915` 为语音版。

本机一键恢复原稳定版（恢复前自动备份当前文件）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\MateEngine-Sola\scripts\Restore-Version.ps1
```

恢复语音版：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\MateEngine-Sola\scripts\Restore-Version.ps1 -Version voice-v1-20260915
```

快照在 `work/releases`，包含源码、已编译 DLL 和本机设置，并带 SHA256 校验；不上传私人设置。回档不删除额外文件，不重置 Git 历史。所有本机快照均留在 D 盘。

Git 源码回档建议创建独立工作树，保留现在的改动：

```powershell
git worktree add ..\mate2-before-voice stable-before-voice-20260915
git worktree add ..\mate2-voice-v1 voice-v1-20260915
```

新电脑仅克隆仓库还不能运行：需安装同版本 Mate Engine、语音模型和运行时。第三方完整程序及模型不在仓库内。开发重建需 .NET SDK 10.0.300、Mono.Cecil，以及本机 `work/Assembly-CSharp.original.dll` 与原版后处理 DLL；停止 Mate Engine 后执行 `work/build_support.ps1`。原版 DLL 和依赖位于当前安装及本地备份中。

## 验证

`tests/test_voice.py` 覆盖路由、真实事件状态、认证、流式去重、打断取消合成和空识别恢复。`scripts/verify_services.py` 为原音色 TTS → ASR 回测；`verify_integration.py` 为 Unity 对话 → TTS → 真实音频播放 → 打断及回声消除设备检查；`verify_vad.py` 将合成测试语音送入真实流式 VAD / ASR。测试报告保留在本机 `userdata/voice`。

口型按声音振幅驱动，并非逐音素对齐。自动测试验证了音频播放和 AEC 开关，但真实房间外放回声、你的实际说话习惯仍需要使用时检验；用耳机可减少外放影响。本机已选择 Realtek 麦克风阵列，并保持默认关闭；系统默认的 Oculus 虚拟输入也可在面板切换。只有最近可读取的本机 Codex 任务事件可用于状态判断，远程任务不能据此保证可见。

Mate Engine 来自 <https://github.com/shinyflvre/Mate-Engine>，使用其公开版，不改变授权逻辑。上游程序、运行时和资源保留各自许可。
