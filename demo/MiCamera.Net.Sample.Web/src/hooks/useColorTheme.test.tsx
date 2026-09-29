import { act, renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import { useColorTheme } from "./useColorTheme";

describe("useColorTheme", () => {
    beforeEach(() => {
        window.localStorage.clear();
        delete document.documentElement.dataset.theme;
    });

    it("switches the document theme and remembers the selection", () => {
        const { result } = renderHook(() => useColorTheme());

        expect(result.current.theme).toBe("light");
        expect(document.documentElement.dataset.theme).toBe("light");

        act(() => {
            result.current.toggleTheme();
        });

        expect(result.current.theme).toBe("dark");
        expect(document.documentElement.dataset.theme).toBe("dark");
        expect(window.localStorage.getItem("micamera-net-preview-theme")).toBe("dark");
    });
});
