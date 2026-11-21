/* Create a disposable DNS service with no IPv4 or IPv6 configuration. */
#include <SystemConfiguration/SystemConfiguration.h>
#include <stdbool.h>
#include <stdio.h>

static bool disable_protocol(SCNetworkServiceRef service, CFStringRef type)
{
    SCNetworkProtocolRef protocol = SCNetworkServiceCopyProtocol(service, type);
    if (protocol == NULL)
        return false;
    bool disabled = SCNetworkProtocolSetEnabled(protocol, false);
    CFRelease(protocol);
    return disabled;
}

int main(int argc, char *argv[])
{
    if (argc != 3)
        return 64;
    CFStringRef device = CFStringCreateWithCString(NULL, argv[1], kCFStringEncodingUTF8);
    CFStringRef name = CFStringCreateWithCString(NULL, argv[2], kCFStringEncodingUTF8);
    SCPreferencesRef preferences = SCPreferencesCreate(NULL, CFSTR("VPNRouter CI DNS"), NULL);
    if (device == NULL || name == NULL || preferences == NULL ||
        !SCPreferencesLock(preferences, true)) {
        fprintf(stderr, "could not open fixture network preferences: %s\n", SCErrorString(SCError()));
        return 1;
    }

    SCNetworkSetRef set = SCNetworkSetCopyCurrent(preferences);
    CFArrayRef services = set == NULL ? NULL : SCNetworkSetCopyServices(set);
    SCNetworkInterfaceRef interface = NULL;
    bool name_available = true;
    for (CFIndex i = 0; services != NULL && i < CFArrayGetCount(services); i++) {
        SCNetworkServiceRef service = (SCNetworkServiceRef)CFArrayGetValueAtIndex(services, i);
        CFStringRef existing = SCNetworkServiceGetName(service);
        if (existing != NULL && CFEqual(name, existing)) {
            fprintf(stderr, "fixture network service already exists\n");
            name_available = false;
            break;
        }
        SCNetworkInterfaceRef candidate = SCNetworkServiceGetInterface(service);
        CFStringRef bsd_name = candidate == NULL ? NULL : SCNetworkInterfaceGetBSDName(candidate);
        if (bsd_name != NULL && CFEqual(device, bsd_name))
            interface = candidate;
    }

    SCNetworkServiceRef fixture =
        name_available && interface != NULL ? SCNetworkServiceCreate(preferences, interface) : NULL;
    int success = fixture != NULL &&
        SCNetworkServiceSetName(fixture, name) &&
        SCNetworkServiceEstablishDefaultConfiguration(fixture) &&
        disable_protocol(fixture, kSCNetworkProtocolTypeIPv4) &&
        disable_protocol(fixture, kSCNetworkProtocolTypeIPv6) &&
        SCNetworkServiceSetEnabled(fixture, true) &&
        SCNetworkSetAddService(set, fixture) &&
        SCPreferencesCommitChanges(preferences) &&
        SCPreferencesApplyChanges(preferences);
    if (!success)
        fprintf(stderr, "could not create the isolated DNS fixture service: %s\n", SCErrorString(SCError()));
    SCPreferencesUnlock(preferences);
    if (fixture != NULL)
        CFRelease(fixture);
    if (services != NULL)
        CFRelease(services);
    if (set != NULL)
        CFRelease(set);
    CFRelease(preferences);
    CFRelease(name);
    CFRelease(device);
    return success ? 0 : 1;
}
