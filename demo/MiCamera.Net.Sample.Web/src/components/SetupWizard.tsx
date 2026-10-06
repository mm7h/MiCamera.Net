import { useEffect, useRef, useState } from "react";
import { Alert, Button, Card, Collapse, Empty, Form, Input, InputNumber, Modal, Result, Select, Space, Spin, Steps, Table, Tag, Typography } from "antd";
import { ReloadOutlined } from "@ant-design/icons";
import { MiCameraApiError, type CameraDevice, MiCameraApiClient, type SettingsView, type SetupStatus, type StreamSettings } from "../services/MiCameraApiClient";

interface Props {
    client: MiCameraApiClient | null;
    connection: { apiUrl: string; bearerToken: string };
    onConnected(connection: { apiUrl: string; bearerToken: string }, status: SetupStatus): void;
    initialStep?: number;
    editing?: boolean;
    onComplete(status: SetupStatus): void;
    onCancel?(): void;
}

interface Fields { baseUrl: string; pin?: string; username: string; password?: string; confirmation?: string; apiUrl: string; bearerToken: string }

export function SetupWizard({ client: initialClient, connection, onConnected, initialStep = 0, editing = false, onComplete, onCancel }: Props): React.JSX.Element {
    const [form] = Form.useForm<Fields>();
    const [step, setStep] = useState(initialStep);
    const [client, setClient] = useState(initialClient);
    const connectionRef = useRef(connection);
    const backendRef = useRef(initialClient);
    const [requirePin, setRequirePin] = useState(false);
    const [saved, setSaved] = useState<SettingsView | null>(null);
    const [devices, setDevices] = useState<CameraDevice[]>([]);
    const [streams, setStreams] = useState<StreamSettings[]>([]);
    const [loading, setLoading] = useState(true);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [result, setResult] = useState<SetupStatus | null>(null);
    const [activationPending, setActivationPending] = useState(false);
    const controllerRef = useRef<AbortController | null>(null);
    const [attempt, setAttempt] = useState(0);

    useEffect(() => {
        form.setFieldsValue({ apiUrl: connectionRef.current.apiUrl, bearerToken: connectionRef.current.bearerToken });
        const initialClient = backendRef.current;
        if (initialClient === null) { setLoading(false); return; }
        const controller = new AbortController();
        controllerRef.current = controller;
        setLoading(true);
        setError(null);
        void Promise.all([initialClient.getSettings(controller.signal), initialClient.getSetup(controller.signal)]).then(([settings, status]) => {
            if (controller.signal.aborted) return;
            setSaved(settings);
            setStreams(settings.streams);
            setActivationPending(status.configured && (!status.listening || status.applyPending));
            form.setFieldsValue({ baseUrl: settings.milocoBaseUrl, username: settings.rtspUsername });
        }).catch((reason: unknown) => { if (!controller.signal.aborted) setError(message(reason)); })
            .finally(() => { if (!controller.signal.aborted) setLoading(false); });
        return () => { controller.abort(); controllerRef.current?.abort(); };
    }, [form, attempt]);

    const advanceMiloco = async (): Promise<void> => {
        try { await form.validateFields(["baseUrl", "pin"]); setStep(1); setError(null); } catch { /* Inline form errors. */ }
    };

    const discover = async (connect = false): Promise<void> => {
        try { await form.validateFields(connect ? ["apiUrl", "bearerToken"] : ["baseUrl", "pin"]); } catch { return; }
        const controller = new AbortController();
        controllerRef.current?.abort();
        controllerRef.current = controller;
        setBusy(true); setError(null);
        try {
            const values = form.getFieldsValue(true);
            let candidate = client;
            if (connect) {
                candidate = new MiCameraApiClient(values.apiUrl.trim(), values.bearerToken ?? "");
                const [settings, status] = await Promise.all([candidate.getSettings(controller.signal), candidate.getSetup(controller.signal)]);
                if (controller.signal.aborted) return;
                const previous = connectionRef.current;
                const changed = new URL(values.apiUrl.trim()).toString().replace(/\/+$/, "") !== (URL.canParse(previous.apiUrl) ? new URL(previous.apiUrl).toString().replace(/\/+$/, "") : previous.apiUrl) ||
                    (values.bearerToken ?? "").trim() !== previous.bearerToken.trim();
                setSaved(settings);
                setClient(candidate);
                backendRef.current = candidate;
                setActivationPending(status.configured && (!status.listening || status.applyPending));
                connectionRef.current = { apiUrl: values.apiUrl.trim(), bearerToken: values.bearerToken ?? "" };
                onConnected(connectionRef.current, status);
                if (changed) {
                    setDevices([]); setStreams([]); setRequirePin(true);
                    form.setFieldsValue({ pin: undefined, username: settings.rtspUsername, password: undefined, confirmation: undefined });
                    setStep(0); setError("后端连接已切换，请重新填写 Miloco PIN 后继续。");
                    return;
                }
            }
            if (candidate === null) throw new Error("请先连接后端 API。");
            if (!values.baseUrl || !URL.canParse(values.baseUrl.trim()) || !/^https?:\/\//i.test(values.baseUrl.trim()) ||
                ((requirePin || !saved?.hasMilocoPin) && !/^[0-9]{6}$/.test(values.pin ?? ""))) {
                setStep(0); setError("请先填写有效的 Miloco 地址和六位 PIN。"); return;
            }
            const list = await candidate.discover(values.baseUrl.trim(), values.pin, controller.signal);
            if (controller.signal.aborted) return;
            setDevices(list);
            setStep(2);
        } catch (reason) { if (!controller.signal.aborted) setError(message(reason)); }
        finally { if (!controller.signal.aborted) setBusy(false); }
    };

    const checkStreams = (): boolean => {
        let problem: string | null = null;
        if (streams.length === 0) problem = "请至少选择一路摄像头流。";
        else if (streams.some((stream) => !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(stream.streamId)))
            problem = "StreamId 须为 1–64 位字母、数字、连字符或下划线，首位为字母或数字。";
        else if (new Set(streams.map((stream) => stream.streamId.toLowerCase())).size !== streams.length)
            problem = "StreamId 不能重复（不区分大小写）。";
        else if (streams.some((stream) => !Number.isInteger(stream.channel) || stream.channel < 0 || stream.channel > 2147483647 ||
            !Number.isFinite(stream.nominalFrameRate) || stream.nominalFrameRate < 1 || stream.nominalFrameRate > 120))
            problem = "通道须为非负整数，回退帧率须为 1–120。";
        else if (new Set(streams.map((stream) => `${stream.cameraDeviceId}:${stream.channel}`)).size !== streams.length)
            problem = "同一摄像头通道不能重复配置。";
        else if (streams.some((stream) => !devices.some((device) => device.did === stream.cameraDeviceId)))
            problem = "部分已选设备不在当前列表中，请取消这些设备或刷新列表。";
        setError(problem);
        return problem === null;
    };

    const save = async (): Promise<void> => {
        try { await form.validateFields(["username", "password", "confirmation"]); } catch { return; }
        if (!checkStreams() || saved === null || client === null) return;
        const controller = new AbortController();
        controllerRef.current = controller;
        setBusy(true); setError(null);
        try {
            const values = form.getFieldsValue(true);
            const status = await client.saveSettings({ version: saved.version, baseUrl: values.baseUrl.trim(), pin: values.pin,
                rtspUsername: values.username.trim(), rtspPassword: values.password, streams }, controller.signal);
            if (controller.signal.aborted) return;
            if (!status.listening || status.applyPending) throw new Error("配置已保存，但尚未生效，请重试应用。");
            setResult(status); setStep(4);
            form.setFieldsValue({ pin: undefined, password: undefined, confirmation: undefined });
        } catch (reason) {
            if (controller.signal.aborted) return;
            if (!(reason instanceof MiCameraApiError) || reason.status === 503) {
                try {
                    const status = await client.getSetup(controller.signal);
                    if (status.version === saved.version + 1 && (status.listening && !status.applyPending)) {
                        setResult(status); setStep(4);
                        form.setFieldsValue({ pin: undefined, password: undefined, confirmation: undefined });
                        return;
                    }
                    if (status.version === saved.version + 1 && (!status.listening || status.applyPending)) setActivationPending(true);
                } catch { /* Keep the original actionable error. */ }
            }
            setError(message(reason));
        } finally { if (!controller.signal.aborted) setBusy(false); }
    };

    const activate = async (): Promise<void> => {
        if (client === null) return;
        const controller = new AbortController();
        controllerRef.current = controller;
        setBusy(true); setError(null);
        try {
            const status = await client.activateSetup(controller.signal);
            if (controller.signal.aborted) return;
            if (!status.listening || status.applyPending) throw new Error("配置尚未生效，请重试应用。");
            setResult(status); setStep(4); setActivationPending(false);
            form.setFieldsValue({ pin: undefined, password: undefined, confirmation: undefined });
        } catch (reason) { if (!controller.signal.aborted) setError(message(reason)); }
        finally { if (!controller.signal.aborted) setBusy(false); }
    };

    const updateStream = (index: number, update: Partial<StreamSettings>): void =>
        setStreams((current) => current.map((stream, position) => position === index ? { ...stream, ...update } : stream));
    const createStream = (did: string, channel = 0): StreamSettings => {
        const suggestedId = `camera-${did.replace(/[^A-Za-z0-9_-]/g, "-").slice(-40)}${channel === 0 ? "" : `-${channel}`}`;
        let streamId = suggestedId;
        let suffix = 1;
        while (streams.some((stream) => stream.streamId.toLowerCase() === streamId.toLowerCase())) streamId = `${suggestedId}-${suffix++}`;
        return { cameraDeviceId: did, channel, streamId, codec: "H265", nominalFrameRate: 25 };
    };
    const selectDevices = (keys: React.Key[]): void => {
        const dids = keys.map(String);
        setStreams((current) => dids.flatMap((did) => {
            const existing = current.filter((stream) => stream.cameraDeviceId === did);
            return existing.length > 0 ? existing : [createStream(did)];
        }));
    };
    const streamIdError = (stream: StreamSettings): string | undefined => {
        if (!/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(stream.streamId)) return "不支持中文；须为 1–64 位英文字母、数字、连字符或下划线，首位为字母或数字。";
        if (streams.filter((item) => item.streamId.toLowerCase() === stream.streamId.toLowerCase()).length > 1) return "StreamId 不能重复（不区分大小写）。";
        return undefined;
    };
    const nextChannel = (device: CameraDevice): number => {
        const used = new Set(streams.filter((item) => item.cameraDeviceId === device.did).map((item) => item.channel));
        let channel = 0;
        while (used.has(channel)) channel++;
        return channel;
    };

    return <Modal open title={editing ? "重新配置" : "欢迎使用 MiCamera.Net"} width={900}
        closable={editing && !busy} mask={{ closable: false }} keyboard={editing && !busy}
        onCancel={onCancel} footer={step === 4 ? null : activationPending ? <Button type="primary" loading={busy} onClick={() => void activate()}>重试应用已保存的配置</Button> : <Space>
            {step > 0 && <Button disabled={busy} onClick={() => { setStep(step - 1); setError(null); }}>上一步</Button>}
            {step === 0 && <Button type="primary" disabled={busy || loading} onClick={() => void advanceMiloco()}>下一步</Button>}
            {step === 1 && <Button type="primary" disabled={loading} loading={busy} onClick={() => void discover(true)}>连接并获取摄像头</Button>}
            {step === 2 && <Button type="primary" disabled={busy} onClick={() => { if (checkStreams()) setStep(3); }}>下一步</Button>}
            {step === 3 && <Button type="primary" loading={busy} onClick={() => void save()}>{editing ? "保存配置" : "保存并启用"}</Button>}
        </Space>}>
        <Steps current={step} className="setup-steps" items={[{ title: "配置 Miloco" }, { title: "连接后端 API" }, { title: "选择摄像头" }, { title: "RTSP 凭据" }, { title: "完成" }]} />
        {editing && step < 4 && <Alert className="setup-notice" type="info" showIcon title="保存后立即应用配置，无需重启后端。已有播放连接会关闭，请在主页面重新连接。" />}
        {activationPending && <Alert className="setup-notice" type="warning" showIcon title="配置已保存但尚未生效，请先重试应用已保存的配置。" />}
        {error !== null && <Alert className="setup-notice" type="error" showIcon title={error} action={saved === null ? <Space><Button onClick={() => setAttempt((value) => value + 1)}>重试</Button><Button onClick={() => { setStep(1); setError(null); }}>修改 API 地址</Button></Space> : undefined} />}
        <Spin spinning={loading}>
            <Form form={form} layout="vertical" disabled={busy || activationPending} autoComplete="off">
                {step === 0 && <>
                    <Typography.Paragraph type="secondary">先在 Miloco 页面完成本地初始化和小米账号绑定。此地址必须能被 MiCamera 后端访问。</Typography.Paragraph>
                    <Form.Item label="Miloco BaseUrl" name="baseUrl" rules={[{ required: true, message: "请输入 Miloco 地址。" }, { type: "url", message: "请输入完整的 HTTP/HTTPS 地址。" }, { pattern: /^https?:\/\//i, message: "地址必须使用 HTTP 或 HTTPS 协议。" }]}>
                        <Input placeholder="https://192.168.1.100:8000" />
                    </Form.Item>
                    <Form.Item label="Miloco 六位 PIN 码" name="pin" extra={saved?.hasMilocoPin && !requirePin ? "留空保留已保存的 PIN。" : "填写 Miloco 初始化时设置的六位数字，不是小米账号密码。"}
                        rules={[{ required: requirePin || !saved?.hasMilocoPin, message: "请输入六位 PIN 码。" }, { pattern: /^[0-9]{6}$/, message: "PIN 码须为六位数字。" }]}>
                        <Input.Password visibilityToggle={{ tabIndex: -1 }} inputMode="numeric" maxLength={6} autoComplete="new-password" />
                    </Form.Item>
                </>}
                {step === 1 && <>
                    <Typography.Paragraph type="secondary">此地址用于网页访问 MiCamera 服务，包括摄像头列表、预览、截图和配置保存。它与 Miloco 地址不同；部署环境默认使用当前网站的同源代理，无需填写 Token。</Typography.Paragraph>
                    <Form.Item label="后端 HTTP API 地址" name="apiUrl" rules={[{ required: true, message: "请输入后端 API 地址。" }, { type: "url", message: "请输入完整的 HTTP/HTTPS 地址。" }, { pattern: /^https?:\/\//i, message: "地址必须使用 HTTP 或 HTTPS 协议。" }]}><Input placeholder="http://127.0.0.1:5080" /></Form.Item>
                    <Form.Item label="Bearer Token（可选）" name="bearerToken" extra="API 地址和 Token 仅保存在当前页面内存中。"><Input.Password visibilityToggle={{ tabIndex: -1 }} autoComplete="off" placeholder="仅在后端 API 启用鉴权时填写" /></Form.Item>
                </>}
                {step === 2 && <>
                    <div className="section-heading"><Typography.Paragraph>选择需要推流的设备，并为每个通道设置唯一的 StreamId。</Typography.Paragraph>
                        <Button icon={<ReloadOutlined />} loading={busy} onClick={() => void discover()}>刷新</Button></div>
                    <Table<CameraDevice> size="small" rowKey="did" loading={busy} dataSource={devices} pagination={false} scroll={{ x: 580, y: 260 }}
                        locale={{ emptyText: <Empty description="未发现摄像头，请在 Miloco 检查账号、设备及家庭范围后刷新。" /> }}
                        rowSelection={{ selectedRowKeys: [...new Set(streams.map((stream) => stream.cameraDeviceId))], onChange: selectDevices, getCheckboxProps: () => ({ disabled: busy }) }}
                        columns={[{ title: "摄像头", dataIndex: "name" }, { title: "DID", dataIndex: "did" }, { title: "房间", dataIndex: "roomName" },
                            { title: "状态", render: (_, device) => <Tag color={device.online ? "green" : "default"}>{device.online ? "在线" : "离线/未知"}</Tag> }]} />
                    {devices.filter((device) => streams.some((stream) => stream.cameraDeviceId === device.did)).map((device) =>
                        <Card key={device.did} size="small" title={device.name} className="setup-device">
                            {streams.map((stream, index) => stream.cameraDeviceId === device.did && <div key={`${device.did}-${index}`} className="setup-stream">
                                <div className="setup-stream-id">
                                    <Form.Item label="StreamId" htmlFor={`stream-${index}-id`} required validateStatus={streamIdError(stream) ? "error" : undefined} help={streamIdError(stream)}>
                                        <Input id={`stream-${index}-id`} value={stream.streamId} onChange={(event) => updateStream(index, { streamId: event.target.value })} placeholder="例如 living-room" />
                                    </Form.Item>
                                    <Typography.Text type="secondary">可按房间名或监控区域命名，例如 living-room、front-door。须在整个项目中唯一，不区分大小写。<br />不支持中文；1–64 位英文字母、数字、连字符或下划线，首位必须为字母或数字。</Typography.Text>
                                </div>
                                <Collapse size="small" items={[{ key: "advanced", label: "高级设置", children: <div className="setup-advanced">
                                    <div className="setup-field-row"><Form.Item label="通道" htmlFor={`stream-${index}-channel`} validateStatus={streams.some((item, position) => position !== index && item.cameraDeviceId === device.did && item.channel === stream.channel) ? "error" : undefined} help={streams.some((item, position) => position !== index && item.cameraDeviceId === device.did && item.channel === stream.channel) ? "同一设备通道不能重复。" : undefined}>
                                        <Select id={`stream-${index}-channel`} value={stream.channel}
                                            options={(device.channelCount !== null ? Array.from({ length: device.channelCount }, (_, channel) => channel) :
                                                [...new Set([...Array.from({ length: 8 }, (_, channel) => channel), ...streams.filter((item) => item.cameraDeviceId === device.did).map((item) => item.channel), nextChannel(device)])].sort((a, b) => a - b))
                                                .map((channel) => ({ value: channel, label: `通道 ${channel}`, disabled: streams.some((item, position) => position !== index && item.cameraDeviceId === device.did && item.channel === channel) }))}
                                            onChange={(channel) => updateStream(index, { channel })} />
                                    </Form.Item><Typography.Text type="secondary">选择摄像头镜头通道，默认从 0 开始。{device.channelCount === null && "通道数量未知，请选择镜头对应的通道。"}</Typography.Text></div>
                                    <div className="setup-field-row"><Form.Item label="源编码" htmlFor={`stream-${index}-codec`}><Select id={`stream-${index}-codec`} value={stream.codec} options={[{ value: "H265", label: "H.265" }, { value: "H264", label: "H.264" }]} onChange={(codec) => updateStream(index, { codec })} /></Form.Item><Typography.Text type="secondary">须与摄像头实际输出编码一致。</Typography.Text></div>
                                    <div className="setup-field-row"><Form.Item label="回退帧率" htmlFor={`stream-${index}-fps`}><InputNumber id={`stream-${index}-fps`} min={1} max={120} value={stream.nominalFrameRate} onChange={(fps) => updateStream(index, { nominalFrameRate: fps ?? 25 })} /></Form.Item><Typography.Text type="secondary">用于推流时的帧率回退计算，不改变摄像头实际帧率。</Typography.Text></div>
                                    <Space wrap><Button disabled={device.channelCount !== null && nextChannel(device) >= device.channelCount} onClick={() => setStreams((current) => [...current, createStream(device.did, nextChannel(device))])}>增加通道</Button>
                                        <Button danger disabled={streams.filter((item) => item.cameraDeviceId === device.did).length <= 1} onClick={() => setStreams((current) => current.filter((_, position) => position !== index))}>移除此通道</Button></Space>
                                </div> }]} />
                            </div>)}
                        </Card>)}
                    {streams.some((stream) => !devices.some((device) => device.did === stream.cameraDeviceId)) && <Alert className="setup-notice" type="warning" title="已保存配置包含当前列表中不存在的设备。"
                        action={<Button onClick={() => setStreams((current) => current.filter((stream) => devices.some((device) => device.did === stream.cameraDeviceId)))}>移除缺失设备</Button>} />}
                </>}
                {step === 3 && <>
                    <Typography.Paragraph type="secondary">播放器访问 RTSP 视频流时，需要使用此用户名和密码进行身份验证；未通过认证的客户端无法播放。它们与 Miloco PIN、后端 API Token 不同。</Typography.Paragraph>
                    <Form.Item label="RTSP 用户名" name="username" rules={[{ required: true, message: "请输入用户名。" }, { pattern: /^[A-Za-z0-9._-]{1,64}$/, message: "使用 1–64 位字母、数字、点、下划线或连字符。" }]}><Input maxLength={64} /></Form.Item>
                    <Form.Item label="RTSP 密码" name="password" dependencies={["username"]} extra={saved?.hasRtspPassword ? "留空保留密码；修改用户名时必须设置新密码。" : undefined}
                        rules={[({ getFieldValue }) => ({ validator: (_, value: string | undefined) => {
                            if (!value && (!saved?.hasRtspPassword || getFieldValue("username") !== saved.rtspUsername)) return Promise.reject(new Error("请输入密码。"));
                            if (value && (value.trim().length === 0 || value.length > 256 || Array.from(value).some((character) => character.charCodeAt(0) < 32 || (character.charCodeAt(0) >= 127 && character.charCodeAt(0) <= 159)))) return Promise.reject(new Error("密码须为 1–256 位，不能为纯空白或包含控制字符。"));
                            return Promise.resolve();
                        } })]}><Input.Password visibilityToggle={{ tabIndex: -1 }} maxLength={256} autoComplete="new-password" /></Form.Item>
                    <Form.Item label="确认密码" name="confirmation" dependencies={["password"]} rules={[({ getFieldValue }) => ({ validator: (_, value) =>
                        (value ?? "") === (getFieldValue("password") ?? "") ? Promise.resolve() : Promise.reject(new Error("两次输入的密码不一致。")) })]}><Input.Password visibilityToggle={{ tabIndex: -1 }} maxLength={256} autoComplete="new-password" /></Form.Item>
                </>}
            </Form>
            {step === 4 && result !== null && <Result status="success" title={editing ? "配置已生效" : "设置完成，Enjoy"}
                subTitle="配置已启用，请在主页面访问摄像头。"
                extra={<Button type="primary" onClick={() => onComplete(result)}>进入主页面</Button>} />}
        </Spin>
    </Modal>;
}

function message(reason: unknown): string { return reason instanceof Error ? reason.message : "操作失败，请重试。"; }
