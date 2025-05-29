## Kinect V1 Spout Sender
This is a simple project for my personal VJing needs.
It captures input from a Kinect V1 sensor, removes the background, and sends the resulting video stream over Spout.

**Note**: It crashes on launch quite frequently. To the best of my knowledge, this is because of an issue with the C# spout wrapper:

https://github.com/Ruminoid/Spout.NET/issues/3

I've done my best to work around it, but it still happens. If you can fix it, please let me know!
This is really only a temporary solution so I can develop VJ graphics until I get a Kinect V2 sensor.

If you have questions about it, feel free to message me or open an issue. Don't expect support or even for it to work really lol.

**One other note**: If your system has multiple GPUs, you'll need to make sure that both your sender and your receiver are running on the same card.
You can change this in your windows graphics settings, but I've had better luck forcing it with Nvidia Control Panel.

This project depends on several other things I should give credit to:
- spout.NET: https://github.com/Ruminoid/Spout.NET/ for the C# spout wrapper.
- The Microsoft Kinect SDK: https://www.microsoft.com/en-us/download/details.aspx?id=44561 for the Kinect V1 SDK.
