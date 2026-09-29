import type { FormEvent } from "react";

interface ConnectionSettingsProps {
    apiUrl: string;
    bearerToken: string;
    busy: boolean;
    onApiUrlChange(value: string): void;
    onBearerTokenChange(value: string): void;
    onSubmit(): void;
}

export function ConnectionSettings({
    apiUrl,
    bearerToken,
    busy,
    onApiUrlChange,
    onBearerTokenChange,
    onSubmit
}: ConnectionSettingsProps): React.JSX.Element {
    const handleSubmit = (event: FormEvent<HTMLFormElement>): void => {
        event.preventDefault();
        onSubmit();
    };

    return (
        <form className="connection-settings card" onSubmit={handleSubmit}>
            <div className="section-heading">
                <div>
                    <p className="eyebrow">连接设置</p>
                    <h2>MiCamera HTTP API</h2>
                </div>
                <button className="primary" type="submit" disabled={busy}>
                    {busy ? "正在连接…" : "保存并刷新"}
                </button>
            </div>
            <label>
                API 地址
                <input
                    type="url"
                    value={apiUrl}
                    onChange={(event) => onApiUrlChange(event.target.value)}
                    placeholder="http://127.0.0.1:5080"
                    required
                />
            </label>
            <label>
                Bearer Token（可选）
                <input
                    type="password"
                    value={bearerToken}
                    onChange={(event) => onBearerTokenChange(event.target.value)}
                    autoComplete="off"
                    placeholder="仅在 API 启用鉴权时填写"
                />
            </label>
            <p className="help-text">Token 仅保存在当前页面内存中，刷新页面后会清除。</p>
        </form>
    );
}
