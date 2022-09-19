#pragma once
#include "Mod.hpp"

#include <REFramework.hpp>

class ScriptConsole final : public Mod {
public:
    static std::shared_ptr<ScriptConsole> get();

    ScriptConsole();

    ScriptConsole(const ScriptConsole&) = delete;
    ScriptConsole& operator=(const ScriptConsole&) = delete;
    ScriptConsole(ScriptConsole&&) = delete;
    ScriptConsole& operator=(ScriptConsole&&) = delete;

    ~ScriptConsole() override;

    std::string_view get_name() const override;
    std::optional<std::string> on_initialize() override;

    void on_lua_state_created(sol::state& lua) override;
    void on_lua_state_destroyed(sol::state& lua) override;

    void on_draw_ui() override;

    bool on_message(HWND wnd, UINT message, WPARAM w_param, LPARAM l_param) override;

    void repl();

private:
    sol::state* m_lua{nullptr};

    std::string m_current_statement;
    sol::environment m_environment;

    std::vector<std::string> m_autocomplete_options;
    const std::vector<std::string> m_relevant_modules = {
        "imgui", "sdk", "reframework", "re", "imguizmo", "json", "draw", "fs", "io", "log"
    };
};