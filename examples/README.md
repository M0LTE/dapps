# Examples

First-party example apps that build against a running DAPPS instance.

| Example | Language / runtime | What it demonstrates |
|---------|---------------------|----------------------|
| [`file-transfer/`](file-transfer/) | Browser (HTML + vanilla JS, mqtt.js over WebSocket) | Binary file transfer with inline preview for browser-friendly types. Whole-file send (DAPPS handles fragmentation). MQTT-over-WebSocket. |

Smaller Python scripts (chat, hello, sensor, pager) live in [`docs/examples/`](../docs/examples/) - those ship alongside the [app-developer guide](https://m0lte.github.io/dapps/app-developers/) and exercise MQTT-over-TCP via paho-mqtt.

If you write your own example and think it'd be useful to other operators, [open an issue](https://github.com/M0LTE/dapps/issues) and we'll add it here.
