#pragma once

#include <functional>
#include <memory>
#include <vector>

#include <Windows.h>

class WindowsConsoleHook {
public:
    static std::shared_ptr<WindowsConsoleHook> get();

    WindowsConsoleHook();
    WindowsConsoleHook(const WindowsConsoleHook& other) = delete;
    WindowsConsoleHook(WindowsConsoleHook&& other) = delete;
    virtual ~WindowsConsoleHook();

    bool install();
    bool remove();

    bool is_valid() const { return m_active; }

    void on_ctrl_c(std::function<void()> listener);

    WindowsConsoleHook& operator=(const WindowsConsoleHook& other) = delete;
    WindowsConsoleHook& operator=(const WindowsConsoleHook&& other) = delete;

    void notify_listeners(DWORD signal) const;

private:
    bool m_active{false};

    std::vector<std::function<void()>> m_ctrl_c_listeners{};
};