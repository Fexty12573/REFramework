#include "ScriptConsole.hpp"

#include "lstate.h"
#include "spdlog/sinks/sink.h"
#include "spdlog/sinks/stdout_color_sinks-inl.h"

#include <WindowsConsoleHook.hpp>

#include <sol/sol.hpp>
#include <linenoise.h>

std::shared_ptr<ScriptConsole> ScriptConsole::get() {
    static auto instance = std::make_shared<ScriptConsole>();
    return instance;
}

ScriptConsole::ScriptConsole() {
    m_autocomplete_options = {
        "and", "break", "do",
        "else", "elseif", "end",
        "false", "for", "function",
        "if", "in", "local",
        "nil", "not", "or",
        "repeat", "return", "then",
        "true", "until", "while", "goto"
    };
}

ScriptConsole::~ScriptConsole() {
    m_lua = nullptr;
}

std::string_view ScriptConsole::get_name() const {
    return "ScriptConsole";
}

std::optional<std::string> ScriptConsole::on_initialize() {
    return Mod::on_initialize();
}

void ScriptConsole::on_lua_state_created(sol::state& lua) {
    m_lua = &lua;
    m_environment = sol::environment(lua, sol::create, lua.globals());
}

void ScriptConsole::on_lua_state_destroyed(sol::state& lua) {
    m_lua = nullptr;
    m_environment = sol::environment{};
}

void ScriptConsole::on_draw_ui() {
    ImGui::SetNextTreeNodeOpen(false, ImGuiCond_Once);

    if (ImGui::CollapsingHeader(get_name().data())) {
        if (ImGui::Button("Open Interactive Console")) {
            if (GetConsoleWindow() == nullptr) {
                AllocConsole();

                freopen("CONIN$", "r", stdin);
                freopen("CONOUT$", "w", stdout);
                freopen("CONOUT$", "w", stderr);

                // We have to reapply the hook every time the console is opened
                WindowsConsoleHook::get()->install();
                WindowsConsoleHook::get()->on_ctrl_c([this] {
                    std::cin.putback('\n');
                    m_current_statement.clear();
                });

                const auto logger = spdlog::get("REFramework");
                if (typeid(logger->sinks().back()) != typeid(spdlog::sinks::stderr_color_sink_mt)) {
                    // Add a sink to spdlog to print to the console
                    const auto sink = std::make_shared<spdlog::sinks::stderr_color_sink_mt>();
                    sink->set_level(spdlog::level::warn);
                    logger->sinks().push_back({sink});
                }

                std::thread t(&ScriptConsole::repl, this);
                t.detach();

            } else {
                MessageBoxA(nullptr, "Close the currently opened console first!", "Error", MB_OK | MB_ICONEXCLAMATION);
            }
        }
    }
}

bool ScriptConsole::on_message(HWND wnd, UINT message, WPARAM w_param, LPARAM l_param) {
    return true;
}

void ScriptConsole::repl() {
    m_environment.clear();
    m_environment.set_on(m_lua->globals());

    const auto lua_tostring_func = m_lua->get<sol::protected_function>("tostring");
    const auto to_string = [lua_tostring_func](const sol::object& obj) -> std::string { return lua_tostring_func(obj); };

    (*m_lua)["print"] = [to_string](const sol::object& obj) { std::cout << to_string(obj) << std::endl; };

    linenoiseHistorySetMaxLen(30);
    linenoiseSetCompletionCallback([this](const char* input, linenoiseCompletions* lc) {
        std::string_view last_word = input;
        last_word = last_word.substr(last_word.find_last_of('.') + 1);

        for (const auto& option : m_autocomplete_options) {
            if (option.find(last_word) == 0) {
                linenoiseAddCompletion(lc, option.c_str());
            }
        }
    });

    for (const auto& module : m_relevant_modules) {
        for (const auto& [name, value] : m_lua->get<sol::table>(module)) {
            if (value.get_type() == sol::type::function) {
                std::cout << module << "." << name.as<std::string>() << std::endl;
                m_autocomplete_options.push_back(name.as<std::string>());
            }
        }
    }
    
    try {
        while (true) {

            const auto prompt = linenoise(m_current_statement.empty() ? ">>> " : "... ");
            std::string s{prompt};

            free(prompt);

            //std::cout << (m_current_statement.empty() ? ">>> " : "... ");
            //std::getline(std::cin, s);

            if (s == "exit" || s == "quit") {
                break;
            }

            if (!m_current_statement.empty()) {
                m_current_statement += s + "\n";
            } else {
                m_current_statement = s;
            }

            if (m_environment[m_current_statement] != sol::lua_nil) {
                // Reproduce python interactive mode behavior
                // >>> a = 1
                // >>> a
                // 1
                std::cout << to_string(m_environment[m_current_statement]) << std::endl;
                m_current_statement.clear();
                continue;
            }

            linenoiseHistoryAdd(s.c_str());
            const auto result = m_lua->load(m_current_statement);
            if (!result.valid()) {
                const std::string err = lua_tostring(m_lua->lua_state(), -1);

                if (!err.ends_with("<eof>")) {
                    m_current_statement.clear();
                }
            } else {
                sol::protected_function_result fn_result{};

                try {
                    fn_result = m_lua->script(m_current_statement, m_environment);
                } catch (const sol::error& e) {
                    spdlog::error(e.what());
                    std::cout << e.what() << std::endl;
                    m_current_statement.clear();
                    continue;
                }
                
                if (!fn_result.valid()) {
                    const std::string err = lua_tostring(m_lua->lua_state(), -1);
                    std::cout << err << std::endl;
                } else {
                    switch (fn_result.get_type()) {
                    case sol::type::none:
                        [[fallthrough]];
                    case sol::type::nil:
                        break;
                    case sol::type::string:
                        std::cout << fn_result.get<std::string>() << std::endl;
                        break;
                    case sol::type::number:
                        std::cout << fn_result.get<double>() << std::endl;
                        break;
                    case sol::type::boolean:
                        std::cout << std::boolalpha << fn_result.get<bool>() << std::endl;
                        break;
                    case sol::type::thread:
                        [[fallthrough]];
                    case sol::type::function:
                        [[fallthrough]];
                    case sol::type::userdata:
                        [[fallthrough]];
                    case sol::type::lightuserdata:
                        [[fallthrough]];
                    case sol::type::table:
                        [[fallthrough]];
                    case sol::type::poly:
                        std::cout << to_string(fn_result.get<sol::object>()) << std::endl;
                        break;
                    }
                }

                m_current_statement.clear();
            }
        }
    } catch (const std::exception& e) {
        spdlog::error("Error in ScriptConsole: {}", e.what());
    }
}
