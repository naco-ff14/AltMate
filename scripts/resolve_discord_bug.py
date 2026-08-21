#!/usr/bin/env python3
"""Reply to an original Discord report after its AltMate fix is released."""

from __future__ import annotations

import os
import re
import subprocess
import sys
import urllib.parse
from typing import Any

from discord_bug_intake import (
    DiscordClient,
    GitHubClient,
    MESSAGE_ID_PATTERN,
    THREAD_ID_PATTERN,
    management_id,
    required_environment,
)


RESOLUTION_MARKER = "discord_resolution_notified: true"
UNAVAILABLE_MARKER = "discord_resolution_unavailable: true"
DISCORD_MESSAGE_URL_PATTERN = re.compile(
    r"https://(?:www\.)?discord\.com/channels/\d+/(\d+)/\d+"
)
MANAGEMENT_ID_PATTERN = re.compile(r"\bAM-0*(\d+)\b", re.IGNORECASE)
RESOLVED_EMOJI_NAMES = ("edit", "修正済", "修正済み", "fixed", "resolved", "shuseizumi")
RESOLVED_FORUM_TAG_NAME = "修正済"
FORUM_STATUS_TAG_NAMES = {"確認中", "修正対応中", "修正済", "見送り", "情報不足"}


def issue_number(value: str) -> int:
    match = re.fullmatch(r"(?:AM-)?0*(\d+)", value.strip(), re.IGNORECASE)
    if not match or int(match.group(1)) <= 0:
        raise ValueError("Issue number must be a positive number or an AM-0001 identifier.")
    return int(match.group(1))


def release_tag(value: str) -> str:
    version = value.strip().removeprefix("v")
    if not re.fullmatch(r"\d+(?:\.\d+){1,3}", version):
        raise ValueError("Release version must have a numeric format such as 1.31.0.2.")
    return f"v{version}"


def referenced_issue_numbers(text: str) -> list[int]:
    return list(dict.fromkeys(int(match) for match in MANAGEMENT_ID_PATTERN.findall(text)))


def report_reply_channel(body: str, fallback_channel_id: str) -> str:
    thread_match = THREAD_ID_PATTERN.search(body)
    if thread_match:
        return thread_match.group(1)
    source_link = DISCORD_MESSAGE_URL_PATTERN.search(body)
    return source_link.group(1) if source_link else fallback_channel_id


def issue_fix_summary(issue: dict[str, Any]) -> str:
    title = str(issue.get("title") or "").strip()
    report_title = title.partition("｜")[2].strip() or title
    return f"「{report_title}」の問題を修正しました。"


def resolved_forum_tags(
    available_tags: list[dict[str, Any]], applied_tags: list[str]
) -> list[str]:
    resolved = next(
        (
            tag
            for tag in available_tags
            if str(tag.get("name") or "").strip().endswith(RESOLVED_FORUM_TAG_NAME)
            and tag.get("id")
        ),
        None,
    )
    if resolved is None:
        available_names = ", ".join(
            repr(str(tag.get("name") or "")) for tag in available_tags
        ) or "none"
        raise RuntimeError(
            f'The Discord bug-report forum has no "{RESOLVED_FORUM_TAG_NAME}" tag. '
            f"Available tags: {available_names}."
        )

    status_ids = {
        str(tag["id"])
        for tag in available_tags
        if any(
            str(tag.get("name") or "").strip().endswith(status)
            for status in FORUM_STATUS_TAG_NAMES
        )
        and tag.get("id")
    }
    preserved = [str(tag_id) for tag_id in applied_tags if str(tag_id) not in status_ids]
    return [*preserved, str(resolved["id"])]


def apply_resolved_forum_tag(
    discord: DiscordClient, channel: dict[str, Any], reply_channel_id: str
) -> bool:
    parent_id = str(channel.get("parent_id") or "")
    if not parent_id:
        print("The original Discord report is not a forum post; no forum tag was changed.")
        return False

    forum = discord.get(f"/channels/{parent_id}")
    if forum.get("type") != 15:
        print("The original Discord thread does not belong to a forum; no forum tag was changed.")
        return False

    current_tags = [str(tag_id) for tag_id in channel.get("applied_tags", [])]
    updated_tags = resolved_forum_tags(forum.get("available_tags", []), current_tags)
    if current_tags != updated_tags:
        discord.patch(f"/channels/{reply_channel_id}", {"applied_tags": updated_tags})
        print(f'Updated the Discord forum tag to "{RESOLVED_FORUM_TAG_NAME}".')
    else:
        print(f'The Discord forum already has the "{RESOLVED_FORUM_TAG_NAME}" tag.')
    return True


def mark_issue_resolved(body: str, tag: str, summary: str, release_url: str) -> str:
    body = re.sub(r"(?m)^- 状態: (未確認|調査中)$", "- 状態: 修正済み", body, count=1)
    result = (
        "\n\n## 修正結果\n\n"
        f"- 修正バージョン: {tag}\n"
        f"- 修正内容: {summary}\n"
        f"- リリース: {release_url}\n"
    )
    if "-->" not in body:
        raise ValueError("The support issue is missing its Discord metadata block.")
    body = body.replace("\n-->", f"\n{RESOLUTION_MARKER}\n-->", 1)
    previous_result = body.find("\n\n## 修正結果")
    if previous_result >= 0:
        body = body[:previous_result]
    return body + result


def resolution_message(
    number: int, tag: str, summary: str, original_message_id: str
) -> dict[str, Any]:
    return {
        "content": (
            f"✅ **{management_id(number)} 修正完了**\n"
            f"{summary}\n"
            f"詳しくは #更新情報 をご確認ください。({tag})"
        ),
        "message_reference": {
            "message_id": original_message_id,
            "fail_if_not_exists": True,
        },
        "allowed_mentions": {"parse": [], "replied_user": False},
    }


def resolved_emoji(emojis: list[dict[str, Any]]) -> str:
    for expected_name in RESOLVED_EMOJI_NAMES:
        emoji = next(
            (
                candidate
                for candidate in emojis
                if candidate.get("name", "").casefold() == expected_name.casefold()
                and candidate.get("id")
                and candidate.get("available", True)
            ),
            None,
        )
        if emoji:
            return urllib.parse.quote(f"{emoji['name']}:{emoji['id']}", safe="")

    supported_names = ", ".join(RESOLVED_EMOJI_NAMES)
    raise RuntimeError(
        "The Discord server has no available resolved emoji. "
        f"Register a custom emoji named one of: {supported_names}."
    )


def run_self_test() -> None:
    assert issue_number("1") == 1
    assert issue_number("AM-0001") == 1
    assert issue_number("am-0012") == 12
    assert release_tag("1.31.0.2") == "v1.31.0.2"
    assert release_tag("v1.31.0.2") == "v1.31.0.2"
    assert referenced_issue_numbers("Fix AM-0002 and am-0012; AM-0002") == [2, 12]
    assert report_reply_channel("discord_thread_id: 456", "999") == "456"
    assert report_reply_channel(
        "https://discord.com/channels/111/222/333", "999"
    ) == "222"
    assert report_reply_channel("Discord metadata unavailable", "999") == "999"
    assert issue_fix_summary({"title": "AM-0002｜表示の不具合"}) == "「表示の不具合」の問題を修正しました。"
    forum_tags = [
        {"id": "10", "name": "🔍 確認中"},
        {"id": "20", "name": "🔧 修正対応中"},
        {"id": "30", "name": "✅ 修正済"},
        {"id": "40", "name": "その他"},
    ]
    assert resolved_forum_tags(forum_tags, ["10", "40"]) == ["40", "30"]
    assert resolved_forum_tags(forum_tags, ["20", "30"]) == ["30"]
    assert resolved_forum_tags(forum_tags, []) == ["30"]
    try:
        resolved_forum_tags([{"id": "10", "name": "確認中"}], ["10"])
        raise AssertionError("A missing resolved forum tag should fail.")
    except RuntimeError:
        pass
    body = "- 状態: 未確認\n<!-- altmate-support\ndiscord_message_id: 123\ndiscord_thread_id: 456\n-->"
    updated = mark_issue_resolved(body, "v1.0.0", "修正しました", "https://example.com")
    assert "- 状態: 修正済み" in updated
    assert RESOLUTION_MARKER in updated
    assert "- 修正バージョン: v1.0.0" in updated
    existing_result = body + "\n\n## 修正結果\n\n- 修正バージョン: v0.9.0\n"
    refreshed = mark_issue_resolved(existing_result, "v1.0.0", "修正しました", "https://example.com")
    assert refreshed.count("## 修正結果") == 1
    assert "v0.9.0" not in refreshed
    assert RESOLUTION_MARKER in refreshed
    reply = resolution_message(1, "v1.0.0", "修正しました", "123")
    assert reply["message_reference"]["message_id"] == "123"
    assert reply["allowed_mentions"]["replied_user"] is False
    assert "#更新情報" in reply["content"]
    assert "https://" not in reply["content"]
    assert resolved_emoji([{"name": "edit", "id": "321"}]) == "edit%3A321"
    assert resolved_emoji(
        [{"name": "fixed", "id": "456"}, {"name": "edit", "id": "321"}]
    ) == "edit%3A321"
    assert resolved_emoji([{"name": "fixed", "id": "456"}]) == "fixed%3A456"
    assert resolved_emoji([{"name": "修正済", "id": "789"}]).endswith("%3A789")
    try:
        resolved_emoji([{"name": "other", "id": "123"}])
        raise AssertionError("A missing resolved emoji should fail.")
    except RuntimeError:
        pass
    print("Resolution self-test passed.")


def resolve_issue(
    github: GitHubClient,
    discord: DiscordClient,
    number: int,
    tag: str,
    channel_id: str,
    summary: str | None = None,
) -> None:
    release = github.get(f"{github.repository_path}/releases/tags/{tag}")
    if release.get("draft") or release.get("tag_name") != tag:
        raise RuntimeError(f"AltMate release {tag} has not been published.")

    issue = github.get(f"{github.repository_path}/issues/{number}")
    summary = summary or issue_fix_summary(issue)
    body = issue.get("body") or ""
    if UNAVAILABLE_MARKER in body:
        print(f"{management_id(number)} has already been resolved and announced.")
        return

    match = MESSAGE_ID_PATTERN.search(body)
    if not match:
        raise RuntimeError(f"{management_id(number)} does not contain a Discord message ID.")

    reply_channel_id = report_reply_channel(body, channel_id)
    try:
        channel = discord.get(f"/channels/{reply_channel_id}")
    except RuntimeError as error:
        if "HTTP 404" not in str(error) or "Unknown Channel" not in str(error):
            raise
        unavailable_body = mark_issue_resolved(
            body, tag, summary, release["html_url"]
        ).replace(RESOLUTION_MARKER, UNAVAILABLE_MARKER, 1)
        unavailable_body += "- Discord通知: 元投稿チャンネルが削除済みのため送信できませんでした。\n"
        labels = [
            label["name"]
            for label in issue.get("labels", [])
            if label["name"] not in {"未確認", "調査中", "対応不要"}
        ]
        if "修正済み" not in labels:
            labels.append("修正済み")
        github.post(
            f"{github.repository_path}/issues/{number}/comments",
            {
                "body": (
                    f"## 修正完了\n\n{summary}\n\n"
                    f"- 修正バージョン: {tag}\n"
                    "- Discord元投稿チャンネルは削除済みのため、返信とリアクションは省略しました。"
                )
            },
        )
        github.patch(
            f"{github.repository_path}/issues/{number}",
            {
                "body": unavailable_body,
                "labels": labels,
                "state": "closed",
                "state_reason": "completed",
            },
        )
        print(
            f"Resolved {management_id(number)} in {tag}; "
            "the original Discord channel no longer exists."
        )
        return
    guild_id = channel.get("guild_id")
    if not guild_id:
        raise RuntimeError("The Discord bug-report channel does not belong to a server.")
    if RESOLUTION_MARKER in body:
        apply_resolved_forum_tag(discord, channel, reply_channel_id)
        print(f"{management_id(number)} has already been resolved and announced.")
        return
    emoji = resolved_emoji(discord.get(f"/guilds/{guild_id}/emojis"))
    forum_tag_updated = apply_resolved_forum_tag(discord, channel, reply_channel_id)

    discord.post(
        f"/channels/{reply_channel_id}/messages",
        resolution_message(number, tag, summary, match.group(1)),
    )
    discord.put(
        f"/channels/{reply_channel_id}/messages/{match.group(1)}/reactions/{emoji}/@me"
    )
    github.post(
        f"{github.repository_path}/issues/{number}/comments",
        {
            "body": (
                f"## 修正完了\n\n{summary}\n\n"
                f"- 修正バージョン: {tag}\n"
                f"- リリース: {release['html_url']}\n"
                "- Discord元投稿へ修正完了を返信済み\n"
                "- Discord元投稿へ修正済の絵文字を追加済み"
                + (
                    f'\n- Discordフォーラムのタグを「{RESOLVED_FORUM_TAG_NAME}」へ変更済み'
                    if forum_tag_updated
                    else ""
                )
            )
        },
    )
    labels = [
        label["name"]
        for label in issue.get("labels", [])
        if label["name"] not in {"未確認", "調査中", "対応不要"}
    ]
    if "修正済み" not in labels:
        labels.append("修正済み")

    github.patch(
        f"{github.repository_path}/issues/{number}",
        {
            "body": mark_issue_resolved(body, tag, summary, release["html_url"]),
            "labels": labels,
            "state": "closed",
            "state_reason": "completed",
        },
    )
    print(f"Resolved {management_id(number)} in {tag} and replied to the original Discord message.")


def release_commit_messages() -> str:
    previous_tag = subprocess.run(
        ["git", "describe", "--tags", "--abbrev=0", "HEAD^"],
        capture_output=True,
        text=True,
        check=False,
    )
    commit_range = (
        f"{previous_tag.stdout.strip()}..HEAD" if previous_tag.returncode == 0 else "HEAD"
    )
    return subprocess.run(
        ["git", "log", commit_range, "--format=%B"],
        capture_output=True,
        text=True,
        check=True,
    ).stdout


def main() -> None:
    resolve_release = "--resolve-release" in sys.argv
    numbers = referenced_issue_numbers(release_commit_messages()) if resolve_release else []
    if resolve_release and not numbers:
        print("No AM issue numbers were found in the released commits.")
        return

    github = GitHubClient(
        required_environment("GITHUB_TOKEN"), required_environment("GITHUB_REPOSITORY")
    )
    discord = DiscordClient(required_environment("DISCORD_BOT_TOKEN"))
    channel_id = required_environment("DISCORD_BUG_CHANNEL_ID")
    configured_version = os.environ.get("RELEASE_VERSION", "").strip()
    if configured_version:
        tag = release_tag(configured_version)
    else:
        latest = github.get(f"{github.repository_path}/releases/latest")
        tag = release_tag(str(latest.get("tag_name") or ""))

    if resolve_release:
        for number in numbers:
            resolve_issue(github, discord, number, tag, channel_id)
    else:
        resolve_issue(
            github,
            discord,
            issue_number(required_environment("ISSUE_NUMBER")),
            tag,
            channel_id,
            os.environ.get("FIX_SUMMARY", "").strip() or None,
        )


if __name__ == "__main__":
    try:
        if "--self-test" in sys.argv:
            run_self_test()
        else:
            main()
    except Exception as error:
        print(f"::error::{error}", file=sys.stderr)
        raise SystemExit(1) from error
