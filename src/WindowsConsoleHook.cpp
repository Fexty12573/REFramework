#include "WindowsConsoleHook.hpp"

#include <spdlog/spdlog.h>

BOOL control_handler(DWORD ctrl_type) {
    WindowsConsoleHook::get()->notify_listeners(ctrl_type);

    switch (ctrl_type) {
    case CTRL_C_EVENT:
        spdlog::info("CTRL_C_EVENT");
        return TRUE;

    case CTRL_CLOSE_EVENT:
        spdlog::info("CTRL_CLOSE_EVENT");
        // Prevent game from terminating when closing the console window
        FreeConsole();
        return TRUE;

    default:
        spdlog::info("Control event {}", ctrl_type);
        return FALSE;
    }
}



std::shared_ptr<WindowsConsoleHook> WindowsConsoleHook::get() {
    static auto instance = std::make_shared<WindowsConsoleHook>();
    return instance;
}

WindowsConsoleHook::WindowsConsoleHook() {
    spdlog::info("Initializing WindowsConsoleHook");
    install();
}

WindowsConsoleHook::~WindowsConsoleHook() {
    remove();
}

bool WindowsConsoleHook::install() {
    if (!SetConsoleCtrlHandler(control_handler, TRUE)) {
        spdlog::warn("Failed to install console control handler ({})", GetLastError());
        return false;
    } 

    m_active = true;
    spdlog::info("Installed console signal handler");
    return true;
}

bool WindowsConsoleHook::remove() {
    m_active = false;
    return SetConsoleCtrlHandler(nullptr, FALSE);
}

void WindowsConsoleHook::on_ctrl_c(std::function<void()> listener) {
    m_ctrl_c_listeners.push_back(std::move(listener));
}

void WindowsConsoleHook::notify_listeners(DWORD signal) const {
    if (signal == CTRL_C_EVENT) {
        for (const auto& listener : m_ctrl_c_listeners) {
            listener();
        }
    }
}
