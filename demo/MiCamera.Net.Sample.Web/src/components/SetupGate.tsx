import { useEffect, useState } from "react";
import { Alert, Button, Card, Spin } from "antd";
import type { MiCameraApiClient, SetupStatus } from "../services/MiCameraApiClient";
import { SetupWizard } from "./SetupWizard";

export function SetupGate({ client, onReady, connection, onConnected }: {
    client: MiCameraApiClient | null; onReady(status: SetupStatus): void;
    connection: { apiUrl: string; bearerToken: string };
    onConnected(connection: { apiUrl: string; bearerToken: string }, status: SetupStatus): void;
}): React.JSX.Element {
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);
    const [setup, setSetup] = useState(false);
    const [attempt, setAttempt] = useState(0);
    const [activationPending, setActivationPending] = useState(false);
    useEffect(() => {
        if (setup) return;
        if (client === null) { setLoading(false); setError("请填写有效的后端 API 地址。"); return; }
        const controller = new AbortController();
        setLoading(true); setError(null); setSetup(false); setActivationPending(false);
        void client.getSetup(controller.signal).then((status) => {
            if (controller.signal.aborted) return;
            if (status.configured && status.listening && !status.applyPending) onReady(status);
            else if (status.configured) { setActivationPending(true); setError("配置已保存，但 尚未生效，请重试应用。"); }
            else { setLoading(false); setSetup(true); }
        }).catch((reason: unknown) => {
            if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : "无法读取初始化状态。");
        }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
        return () => controller.abort();
    }, [client, onReady, attempt, setup]);
    const activate = async (): Promise<void> => {
        if (client === null) return;
        setLoading(true);
        try {
            const status = await client.activateSetup();
            if (status.listening && !status.applyPending) onReady(status);
        } catch (reason) { setError(reason instanceof Error ? reason.message : "启用失败，请重试。"); }
        finally { setLoading(false); }
    };
    return <main className="app-shell setup-shell">
        <Card title="MiCamera.Net · 初始化"><Spin spinning={loading}><p>{loading ? "正在检查服务配置。" : "请完成初始化配置后开始使用。"}</p>
            {error !== null && <Alert showIcon type="error" title={error} action={<Button disabled={loading} onClick={() => activationPending ? void activate() : setAttempt((value) => value + 1)}>{activationPending ? "重试应用" : "重新检查"}</Button>} />}
        {error !== null && !activationPending && <Button className="button-row" onClick={() => setSetup(true)}>修改后端 API 地址</Button>}
        </Spin></Card>
        {setup && <SetupWizard client={client} connection={connection} onConnected={(next, status) => { setError(null); setLoading(false); onConnected(next, status); }} initialStep={error !== null ? 1 : 0} onComplete={onReady} />}
    </main>;
}
