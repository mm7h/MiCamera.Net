import { useCallback, useEffect, useState } from "react";

export type ColorTheme = "dark" | "light";

const StorageKey = "micamera-net-preview-theme";

export interface ColorThemeControl {
    readonly theme: ColorTheme;
    toggleTheme(): void;
}

export function useColorTheme(): ColorThemeControl {
    const [theme, setTheme] = useState<ColorTheme>(getInitialTheme);

    useEffect(() => {
        document.documentElement.dataset.theme = theme;
        window.localStorage.setItem(StorageKey, theme);
    }, [theme]);

    const toggleTheme = useCallback((): void => {
        setTheme((currentTheme) => currentTheme === "light" ? "dark" : "light");
    }, []);

    return { theme, toggleTheme };
}

function getInitialTheme(): ColorTheme {
    const savedTheme = window.localStorage.getItem(StorageKey);
    if (savedTheme === "light" || savedTheme === "dark") {
        return savedTheme;
    }

    return window.matchMedia?.("(prefers-color-scheme: dark)").matches === true ? "dark" : "light";
}
