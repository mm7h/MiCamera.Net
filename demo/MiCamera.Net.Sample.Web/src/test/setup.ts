import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

// Vitest does not expose globals here, so Testing Library cannot register its own
// cleanup hook; without this every render stays mounted for the rest of the file.
afterEach(cleanup);
