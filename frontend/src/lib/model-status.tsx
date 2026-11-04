"use client";

// Carries GET /api/model/status down to the three places that show it: the sidebar
// card, the tile subtitle and the settings panel.
//
// It is a context rather than props because those three sit at different depths and
// none of the components between them care about the value. The default is the mock,
// so a component still renders on its own -- in a test, or with the service down.

import { createContext, useContext } from "react";
import { MODEL_STATUS, type ModelStatus } from "@/lib/mock";

const Ctx = createContext<ModelStatus>(MODEL_STATUS);

export const ModelStatusProvider = Ctx.Provider;

export function useModelStatus(): ModelStatus {
  return useContext(Ctx);
}
