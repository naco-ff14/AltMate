#!/usr/bin/env python3
"""Import Discord forum reports into AltMate issues and acknowledge each report."""

from __future__ import annotations

import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone
from typing import Any


DISCORD_API = "https://discord.com/api/v10"
GITHUB_API = "https://api.github.com"
JAPAN_TIMEZONE = timezone(timedelta(hours=9))
MESSAGE_ID_PATTERN = re.compile(r"discord_message_id:\s*(\d+)")
THREAD_ID_PATTERN = re.compile(r"discord_thread_id:\s*(\d+)")
ACKNOWLEDGEMENT_STATE_PATTERN = re.compile(
    r"discord_(?:acknowledged|forwarded):\s*(true|false)"
)
FORUM_CHANNEL_TYPE = 15
LABELS = {
    "不具合": ("d73a4a", "Discordから取り込んだ不具合報告"),
    "未確認": ("fbca04", "管理者による確認待ち"),
    "調査中": ("1d76db", "原因を調査中"),
    "修正済み": ("0e8a16", "修正が完了"),
    "対応不要": ("cfd3d7", "修正対応は不要"),
}


def required_environment(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise RuntimeError(f"Required environment variable {name} is not configured.")
    return value


def request_json(
    method: str,
    url: str,
    *,
    headers: dict[str, str],
    payload: dict[str, Any] | None = None,
) -> Any:
    body = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request_headers = {"Accept": "application/json", **headers}
    if body is not None:
        request_headers["Content-Type"] = "application/json; charset=utf-8"

    for attempt in range(4):
        request = urllib.request.Request(url, data=body, headers=request_headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                content = response.read()
                return json.loads(content) if content else None
        except urllib.error.HTTPError as error:
            response_body = error.read().decode("utf-8", errors="replace")
            if error.code == 429 and attempt < 3:
                try:
                    retry_after = float(json.loads(response_body).get("retry_after", 2))
                except (ValueError, json.JSONDecodeError):
                    retry_after = 2
                time.sleep(min(retry_after, 15))
                continue
            raise RuntimeError(f"{method} {url} failed with HTTP {error.code}: {response_body}") from error

    raise RuntimeError(f"{method} {url} exceeded its retry limit.")


class DiscordClient:
    def __init__(self, token: str) -> None:
        self.headers = {"Authorization": f"Bot {token}", "User-Agent": "AltMate/1.0"}

    def get(self, path: str) -> Any:
        return request_json("GET", f"{DISCORD_API}{path}", headers=self.headers)

    def post(self, path: str, payload: dict[str, Any]) -> Any:
        return request_json("POST", f"{DISCORD_API}{path}", headers=self.headers, payload=payload)

    def put(self, path: str) -> Any:
        return request_json("PUT", f"{DISCORD_API}{path}", headers=self.headers)


class GitHubClient:
    def __init__(self, token: str, repository: str) -> None:
        self.repository = repository
        self.headers = {
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "AltMate/1.0",
        }

    def get(self, path: str) -> Any:
        return request_json("GET", f"{GITHUB_API}{path}", headers=self.headers)

    def post(self, path: str, payload: dict[str, Any]) -> Any:
        return request_json("POST", f"{GITHUB_API}{path}", headers=self.headers, payload=payload)

    def patch(self, path: str, payload: dict[str, Any]) -> Any:
        return request_json("PATCH", f"{GITHUB_API}{path}", headers=self.headers, payload=payload)

    @property
    def repository_path(self) -> str:
        return f"/repos/{self.repository}"

    def ensure_labels(self) -> None:
        existing = {
            label["name"]
            for label in self.get(f"{self.repository_path}/labels?per_page=100")
        }
        for name, (color, description) in LABELS.items():
            if name not in existing:
                self.post(
                    f"{self.repository_path}/labels",
                    {"name": name, "color": color, "description": description},
                )

    def existing_reports(self) -> dict[str, dict[str, Any]]:
        reports: dict[str, dict[str, Any]] = {}
        for page in range(1, 11):
            issues = self.get(
                f"{self.repository_path}/issues?state=all&per_page=100&page={page}"
            )
            for issue in issues:
                match = MESSAGE_ID_PATTERN.search(issue.get("body") or "")
                if match:
                    reports[match.group(1)] = issue
            if len(issues) < 100:
                break
        return reports


def management_id(issue_number: int) -> str:
    return f"AM-{issue_number:04d}"


def report_summary(message: dict[str, Any]) -> str:
    thread_name = str(message.get("_forum_thread_name") or "").strip()
    if thread_name:
        return thread_name[:77] + "..." if len(thread_name) > 80 else thread_name
    for line in (message.get("content") or "").splitlines():
        line = re.sub(r"^[\s#>*-]+", "", line).strip()
        if line:
            return line[:77] + "..." if len(line) > 80 else line
    return "添付ファイル付きの不具合報告"


def format_japan_time(timestamp: str) -> str:
    parsed = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
    return parsed.astimezone(JAPAN_TIMEZONE).strftime("%Y/%m/%d %H:%M JST")


def message_url(guild_id: str, channel_id: str, message_id: str) -> str:
    return f"https://discord.com/channels/{guild_id}/{channel_id}/{message_id}"


def author_name(message: dict[str, Any]) -> str:
    author = message.get("author") or {}
    return author.get("global_name") or author.get("username") or "不明"


def eligible_message(message: dict[str, Any], bot_id: str) -> bool:
    author = message.get("author") or {}
    if author.get("bot") or str(author.get("id", "")) == bot_id:
        return False
    if message.get("pinned") or message.get("type", 0) not in (0, 19):
        return False
    return bool((message.get("content") or "").strip() or message.get("attachments"))


def report_channel_id(message: dict[str, Any], source_channel_id: str) -> str:
    return str(message.get("_forum_thread_id") or source_channel_id)


def forum_starter_messages(
    discord: DiscordClient, guild_id: str, forum_channel_id: str
) -> list[dict[str, Any]]:
    active = discord.get(f"/guilds/{guild_id}/threads/active").get("threads", [])
    archived = discord.get(
        f"/channels/{forum_channel_id}/threads/archived/public?limit=100"
    ).get("threads", [])
    threads = {
        str(thread["id"]): thread
        for thread in [*active, *archived]
        if str(thread.get("parent_id") or "") == forum_channel_id
    }

    messages: list[dict[str, Any]] = []
    for thread_id, thread in threads.items():
        starter = discord.get(f"/channels/{thread_id}/messages/{thread_id}")
        starter["_forum_thread_id"] = thread_id
        starter["_forum_thread_name"] = thread.get("name") or ""
        messages.append(starter)
    return messages


def issue_body(message: dict[str, Any], guild_id: str, channel_id: str) -> str:
    report_link = message_url(guild_id, channel_id, message["id"])
    content = (message.get("content") or "").strip() or "（本文なし）"
    attachments = message.get("attachments") or []
    attachment_lines = "\n".join(
        f"- [{attachment.get('filename', '添付ファイル')}]({attachment['url']})"
        for attachment in attachments
        if attachment.get("url")
    ) or "- なし"

    return (
        "## 報告内容\n\n"
        f"{content}\n\n"
        "## 受付情報\n\n"
        f"- 投稿者: {author_name(message)}\n"
        f"- 投稿日時: {format_japan_time(message['timestamp'])}\n"
        f"- 元投稿: [Discordメッセージを開く]({report_link})\n\n"
        "## 添付ファイル\n\n"
        f"{attachment_lines}\n\n"
        "## 調査メモ\n\n"
        "- 状態: 未確認\n"
        "- 推定原因: \n"
        "- 関連ファイル: \n"
        "- 対応方針: \n\n"
        "<!-- altmate-discord\n"
        f"discord_message_id: {message['id']}\n"
        f"discord_thread_id: {channel_id}\n"
        "discord_acknowledged: false\n"
        "-->"
    )


def acknowledgement_message(
    message: dict[str, Any], issue: dict[str, Any]
) -> dict[str, Any]:
    return {
        "content": (
            "📋 **不具合報告を受け付けました。**\n"
            f"管理番号: **{management_id(issue['number'])}**\n"
            f"Issue: {issue['html_url']}"
        ),
        "message_reference": {
            "message_id": message["id"],
            "fail_if_not_exists": True,
        },
        "allowed_mentions": {"parse": [], "replied_user": False},
    }


def is_acknowledged(issue: dict[str, Any]) -> bool:
    match = ACKNOWLEDGEMENT_STATE_PATTERN.search(issue.get("body") or "")
    return bool(match and match.group(1) == "true")


def run_self_test() -> None:
    assert management_id(1) == "AM-0001"
    assert management_id(12345) == "AM-12345"
    assert report_summary({"content": "# エーテライト移動で停止\n詳細"}) == "エーテライト移動で停止"
    assert report_summary({"_forum_thread_name": "ホーム画面の表示不具合", "content": "詳細"}) == "ホーム画面の表示不具合"
    assert report_summary({"content": "", "attachments": [{}]}) == "添付ファイル付きの不具合報告"
    assert format_japan_time("2026-08-21T00:30:00+00:00") == "2026/08/21 09:30 JST"
    assert eligible_message({"author": {"id": "1"}, "content": "不具合", "type": 0}, "2")
    assert not eligible_message({"author": {"id": "2", "bot": True}, "content": "自動投稿"}, "2")
    assert not eligible_message({"author": {"id": "1"}, "content": "固定案内", "pinned": True}, "2")
    assert report_channel_id({"_forum_thread_id": "456"}, "123") == "456"
    assert report_channel_id({}, "123") == "123"
    assert is_acknowledged({"body": "<!-- discord_acknowledged: true -->"})
    assert not is_acknowledged({"body": "<!-- discord_acknowledged: false -->"})
    assert is_acknowledged({"body": "<!-- discord_forwarded: true -->"})
    acknowledgement = acknowledgement_message(
        {"id": "123"},
        {"number": 7, "html_url": "https://github.com/naco-ff14/AltMate/issues/7"},
    )
    assert "AM-0007" in acknowledgement["content"]
    assert acknowledgement["message_reference"]["message_id"] == "123"
    assert acknowledgement["allowed_mentions"]["replied_user"] is False
    print("Self-test passed.")


def main() -> None:
    discord = DiscordClient(required_environment("DISCORD_BOT_TOKEN"))
    github = GitHubClient(
        required_environment("GITHUB_TOKEN"), required_environment("GITHUB_REPOSITORY")
    )
    source_channel_id = required_environment("DISCORD_BUG_CHANNEL_ID")
    dry_run = os.environ.get("DRY_RUN", "false").lower() == "true"
    bot = discord.get("/users/@me")
    source_channel = discord.get(f"/channels/{source_channel_id}")
    guild_id = str(source_channel.get("guild_id") or "")
    if not guild_id:
        raise RuntimeError("The Discord bug-report channel must belong to a server.")

    print(f"Bot connected: {bot.get('username', 'unknown')}")
    print(f"Source channel: #{source_channel.get('name', 'unknown')}")
    print(f"Target repository: {github.repository}")

    if source_channel.get("type") == FORUM_CHANNEL_TYPE:
        messages = forum_starter_messages(discord, guild_id, source_channel_id)
        print(f"Forum posts found: {len(messages)}")
    else:
        messages = discord.get(f"/channels/{source_channel_id}/messages?limit=100")
    messages.sort(key=lambda item: int(item["id"]))
    reports = github.existing_reports()
    candidates = [message for message in messages if eligible_message(message, str(bot["id"]))]
    pending = [message for message in candidates if message["id"] not in reports]
    retries = [
        message
        for message in candidates
        if message["id"] in reports and not is_acknowledged(reports[message["id"]])
    ]
    print(
        f"Eligible reports: {len(candidates)}; new: {len(pending)}; "
        f"pending acknowledgements: {len(retries)}"
    )

    if dry_run:
        print("Dry run completed; no issues or Discord replies were created.")
        return

    github.ensure_labels()
    if not pending and not retries:
        print("No new Discord reports found.")
        return

    for message in [*pending, *retries]:
        message_channel_id = report_channel_id(message, source_channel_id)
        issue = reports.get(message["id"])
        if issue is None:
            issue = github.post(
                f"{github.repository_path}/issues",
                {
                    "title": f"受付中｜{report_summary(message)}",
                    "body": issue_body(message, guild_id, message_channel_id),
                    "labels": ["不具合", "未確認"],
                },
            )
            issue = github.patch(
                f"{github.repository_path}/issues/{issue['number']}",
                {"title": f"{management_id(issue['number'])}｜{report_summary(message)}"},
            )
            reports[message["id"]] = issue

        discord.post(
            f"/channels/{message_channel_id}/messages",
            acknowledgement_message(message, issue),
        )
        updated_body = (issue.get("body") or "").replace(
            "discord_acknowledged: false", "discord_acknowledged: true", 1
        )
        github.patch(
            f"{github.repository_path}/issues/{issue['number']}", {"body": updated_body}
        )
        print(f"Imported {management_id(issue['number'])}.")


if __name__ == "__main__":
    try:
        if "--self-test" in sys.argv:
            run_self_test()
        else:
            main()
    except Exception as error:
        print(f"::error::{error}", file=sys.stderr)
        raise SystemExit(1) from error
