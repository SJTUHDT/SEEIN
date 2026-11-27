// GPL-3.0-only. Secrets are tied to the current Windows user, never the shared robot config.
#include "snow_shot/seeinsecret.h"
#include <QString>
#include <QByteArray>
#include <stdexcept>
#ifdef Q_OS_WIN
#include <Windows.h>
#include <wincrypt.h>
#ifdef CopyFile
#undef CopyFile
#endif
#pragma comment(lib, "crypt32.lib")
#endif
namespace snow_shot {
QString seeinProtectSecret(const QString& value) {
    if (value.isEmpty() || qEnvironmentVariable("SEEIN_TOOL_PIPE").isEmpty()) return value;
#ifdef Q_OS_WIN
    auto bytes = value.toUtf8();
    DATA_BLOB input{static_cast<DWORD>(bytes.size()), reinterpret_cast<BYTE*>(bytes.data())}, output{};
    if (!CryptProtectData(&input, L"SEEIN screenshot translation", nullptr, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output))
        throw std::runtime_error("Could not protect translation credentials");
    const auto encoded = QByteArray(reinterpret_cast<const char*>(output.pbData), static_cast<qsizetype>(output.cbData)).toBase64();
    LocalFree(output.pbData);
    return QStringLiteral("seein-dpapi:") + QString::fromLatin1(encoded);
#else
    return value;
#endif
}
QString seeinUnprotectSecret(const QString& value) {
    if (!value.startsWith(QStringLiteral("seein-dpapi:"))) return value;
#ifdef Q_OS_WIN
    auto bytes = QByteArray::fromBase64(value.mid(12).toLatin1());
    DATA_BLOB input{static_cast<DWORD>(bytes.size()), reinterpret_cast<BYTE*>(bytes.data())}, output{};
    if (!CryptUnprotectData(&input, nullptr, nullptr, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output)) return {};
    const auto clear = QString::fromUtf8(reinterpret_cast<const char*>(output.pbData), static_cast<qsizetype>(output.cbData));
    SecureZeroMemory(output.pbData, output.cbData); LocalFree(output.pbData);
    return clear;
#else
    return {};
#endif
}
}
