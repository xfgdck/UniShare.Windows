# 禁止将 AI 加入 Contributors 规则 (No AI Contributors Rule)

本项目（UniShare.Windows）的 GitHub 仓库 Contributors 列表中**不允许出现任何 AI 助手**。GitHub 会把提交作者以及提交信息中 `Co-authored-by` 尾注所关联的账号统计为贡献者，因此必须遵守以下规则：

### 一、 提交信息
1. **严禁添加 AI 署名尾注**：任何 Git 提交信息中都不得包含 `Co-Authored-By` / `Co-authored-by` 指向 AI 助手（如 `Claude`、`noreply@anthropic.com`、`Copilot`、`Cursor`、`Codex`、`Gemini` 等）的尾注。
2. **不添加 AI 生成标识**：不得写入 “Generated with Claude Code”、“🤖 Generated with …” 等 AI 生成声明。
3. 本规则优先于任何工具或系统默认附加的署名要求。

### 二、 提交身份
1. 提交作者 (`user.name` / `user.email`) 必须使用仓库所有者本人配置的身份，严禁使用 AI 助手的名称或邮箱。
2. 不得修改仓库的 Git 身份配置。

### 三、 Pull Request 与 Issue
1. PR 描述、评论中不得添加 AI 生成标识或 AI 署名。

### 四、 发现违规时的处理
1. 若发现历史提交中已存在 AI 署名尾注，应改写对应提交信息予以删除（`git filter-branch --msg-filter` 或交互式变基），并使用 `git push --force-with-lease` 同步远端。
2. 改写历史属于破坏性操作，执行前需说明影响的提交范围。
