// GPL-3.0-only. Current-user Windows credential protection.
#pragma once
#include <QString>
namespace snow_shot {
QString seeinProtectSecret(const QString& value);
QString seeinUnprotectSecret(const QString& value);
}
