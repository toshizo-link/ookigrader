import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { api } from "../lib/api";
import { SchoolManagerMessageTemplates } from "./SchoolManagerMessageTemplates";

const path = "/admin/school-manager/message-templates";
const defaults = {
  title: "暗記テスト採点結果のお知らせ",
  pass: "教科：＜教科＞\n範囲：＜タイトル＞\n結果：＜結果＞",
  fail: "＜タイトル＞を復習しましょう。\n結果：＜結果＞",
  unconfigured: "＜教科＞の答案をご確認ください。",
};
const initial = { ...defaults, revision: 4, defaults };

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

function selectBody(value: string) {
  fireEvent.change(screen.getByLabelText("編集する本文"), { target: { value } });
}

it("saves an entirely custom title and bodies, retaining edits across verdicts and the revision after saving", async () => {
  vi.spyOn(api, "get").mockResolvedValue(initial);
  const put = vi.spyOn(api, "put").mockImplementation(async (_path, data) => ({
    ...initial, ...(data as object), revision: 5,
  }));
  render(<SchoolManagerMessageTemplates />);
  fireEvent.change(await screen.findByLabelText("件名（全判定共通）"), { target: { value: "お知らせ：＜結果＞" } });
  fireEvent.change(screen.getByLabelText("合格の本文"), { target: { value: "よくがんばりました！" } });
  selectBody("fail");
  const newFail = "結果：＜結果＞\n\n再テストでお待ちしています。";
  fireEvent.change(screen.getByLabelText("不合格の本文"), { target: { value: newFail } });
  const view = within(screen.getByRole("region", { name: "配信プレビュー" }));
  expect(view.getByText("お知らせ：不合格")).toBeVisible();
  expect(view.getByText(/再テストで/).textContent).toBe(newFail.replace("＜結果＞", "不合格"));
  selectBody("pass");
  expect(screen.getByLabelText("合格の本文")).toHaveValue("よくがんばりました！");
  fireEvent.click(screen.getByRole("button", { name: "文面を保存" }));
  await screen.findByText("配信文面を保存しました。");
  const edited = { title: "お知らせ：＜結果＞", pass: "よくがんばりました！", fail: newFail, unconfigured: defaults.unconfigured };
  expect(put).toHaveBeenCalledWith(path, { ...edited, revision: 4 });
  selectBody("unconfigured");
  fireEvent.change(screen.getByLabelText("合否基準未設定の本文"), { target: { value: "＜結果＞です" } });
  expect(view.getByText("合否基準未設定です")).toBeVisible();
  fireEvent.click(screen.getByRole("button", { name: "文面を保存" }));
  await waitFor(() => expect(put).toHaveBeenLastCalledWith(path, { ...edited, unconfigured: "＜結果＞です", revision: 5 }));
});

it("inserts a placeholder at the active selection in either field, and previews repeated and unknown markers", async () => {
  vi.spyOn(api, "get").mockResolvedValue({ ...initial, title: "【○】", pass: "＜結果＞ / ○ / ＜任意＞" });
  render(<SchoolManagerMessageTemplates />);
  const title = await screen.findByLabelText<HTMLInputElement>("件名（全判定共通）");
  const view = within(screen.getByRole("region", { name: "配信プレビュー" }));
  fireEvent.focus(title);
  title.setSelectionRange(1, 2);
  fireEvent.click(screen.getByRole("button", { name: "＜教科＞" }));
  expect(title).toHaveValue("【＜教科＞】");
  expect(view.getByText("【理科】")).toBeVisible();
  const body = screen.getByLabelText<HTMLTextAreaElement>("合格の本文");
  fireEvent.focus(body);
  body.setSelectionRange(body.value.indexOf("○"), body.value.indexOf("○") + 1);
  fireEvent.click(screen.getByRole("button", { name: "＜結果＞" }));
  expect(view.getByText("合格 / 合格 / ＜任意＞")).toBeVisible();
  fireEvent.click(screen.getByRole("button", { name: "＜タイトル＞" }));
  expect(view.getByText("合格 / 合格てこ・滑車の確認テスト / ＜任意＞")).toBeVisible();
});

it("restores only the selected text to its standard form before saving", async () => {
  const custom = { ...initial, title: "教室の件名", pass: "教室の合格文", fail: "教室の不合格文" };
  vi.spyOn(api, "get").mockResolvedValue(custom);
  const put = vi.spyOn(api, "put").mockResolvedValue({ ...custom, title: defaults.title, fail: defaults.fail, revision: 5 });
  render(<SchoolManagerMessageTemplates />);
  await screen.findByLabelText("合格の本文");
  selectBody("fail");
  fireEvent.click(screen.getByRole("button", { name: "不合格の本文を標準に戻す" }));
  expect(screen.getByLabelText("不合格の本文")).toHaveValue(defaults.fail);
  expect(screen.getByLabelText("件名（全判定共通）")).toHaveValue("教室の件名");
  selectBody("pass");
  expect(screen.getByLabelText("合格の本文")).toHaveValue("教室の合格文");
  fireEvent.click(screen.getByRole("button", { name: "件名を標準に戻す" }));
  expect(put).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "文面を保存" }));
  await screen.findByText("配信文面を保存しました。");
  expect(put).toHaveBeenCalledWith(path, { ...defaults, pass: custom.pass, revision: 4 });
});

it("identifies a blank body even after switching to another verdict", async () => {
  vi.spyOn(api, "get").mockResolvedValue(initial);
  render(<SchoolManagerMessageTemplates />);
  fireEvent.change(await screen.findByLabelText("合格の本文"), { target: { value: "  " } });
  selectBody("fail");
  expect(screen.getByRole("alert")).toHaveTextContent("合格の本文を入力してください。");
  expect(screen.getByRole("button", { name: "文面を保存" })).toBeDisabled();
});
