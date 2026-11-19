import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { api } from "../lib/api";
import { SchoolManagerMessageTemplates } from "./SchoolManagerMessageTemplates";

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

it("saves separate paragraphs and uses the returned revision on later edits", async () => {
  const initial = { pass: "合格です。", fail: "復習しましょう。", unconfigured: "ご確認ください。", revision: 4 };
  vi.spyOn(api, "get").mockResolvedValue(initial);
  const put = vi.spyOn(api, "put").mockResolvedValue({ ...initial, fail: "もう一度挑戦しましょう。", revision: 5 });
  render(<SchoolManagerMessageTemplates />);
  const field = await screen.findByLabelText("不合格のとき");
  fireEvent.change(field, { target: { value: "もう一度挑戦しましょう。" } });
  fireEvent.click(screen.getByRole("button", { name: "文面を保存" }));
  await screen.findByText("配信文面を保存しました。");
  expect(put).toHaveBeenCalledWith("/admin/school-manager/message-templates",
    { ...initial, fail: "もう一度挑戦しましょう。" });
  fireEvent.change(screen.getByLabelText("合格のとき"), { target: { value: "" } });
  fireEvent.click(screen.getByRole("button", { name: "文面を保存" }));
  await waitFor(() => expect(put).toHaveBeenLastCalledWith("/admin/school-manager/message-templates",
    { pass: "", fail: "もう一度挑戦しましょう。", unconfigured: initial.unconfigured, revision: 5 }));
});
