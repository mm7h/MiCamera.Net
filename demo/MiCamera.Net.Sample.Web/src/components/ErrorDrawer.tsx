import { Alert, Drawer } from "antd";

export interface ErrorNotification { id: number; title: string; message: string }

export function ErrorDrawer({ notification, onClose }: { notification: ErrorNotification | null; onClose(): void }): React.JSX.Element {
    return <Drawer title="信息提醒" open={notification !== null} onClose={onClose}>
        {notification !== null && <Alert type="error" showIcon title={notification.title} description={notification.message} role="alert" />}
    </Drawer>;
}
