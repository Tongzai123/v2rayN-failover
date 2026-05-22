# v2rayN 7.20.4 failover

This project is a customized build based on [2dust/v2rayN](https://github.com/2dust/v2rayN) version `7.20.4`. It keeps the original support for [Xray](https://github.com/XTLS/Xray-core), [sing-box](https://github.com/SagerNet/sing-box), and other supported cores, while adding failover and drag-and-drop sorting features.

This project is not the official `2dust/v2rayN` release. For the official version, documentation, or upstream issue tracking, visit:

- Upstream repository: [2dust/v2rayN](https://github.com/2dust/v2rayN)
- Upstream Wiki: [v2rayN Wiki](https://github.com/2dust/v2rayN/wiki)

中文文档：[README.md](README.md)

## Main Enhancements

### Failover

This build adds transfer groups, an active failover group, and a failover queue. You can add multiple regular nodes to the failover queue and arrange them by priority. After enabling "故障转移" (Failover), the application uses those queued nodes as failover candidates in priority order.

Nodes in the failover queue display priority labels such as `P1` and `P2`. A smaller number means a higher priority. The queue order from top to bottom is the actual priority order.

### Subscription Group Drag-and-Drop Sorting

Subscription group titles can be reordered by drag and drop. Hold a group name, drag it to the target position, and release it to update the display order.

### Node List Drag-and-Drop Sorting

The node list supports drag-and-drop sorting. Nodes inside a transfer group can also be dragged to adjust failover queue priority. Multi-selection drag is supported, and selected nodes move together as one continuous block.

## How to Use Failover

### 1. Create a Transfer Group

Open the subscription group settings, create a new group, and enable the "转移分组" (Transfer Group) switch.

After the group is created, its name is shown as a green title so it can be distinguished from regular groups.

### 2. Set the Active Failover Group

Enter the transfer group you just created, then click "当前组设置为活动故障组" (Set Current Group as Active Failover Group) in the upper-right corner.

After this succeeds, the group becomes the active failover group used by the failover feature.

### 3. Copy Nodes to the Transfer Group

Select the nodes that should participate in failover from a regular group. You can copy them to the transfer group in either of these ways:

- Right-click nodes in a regular group and choose the option to copy them to a transfer group.
- Copy nodes first, switch to the transfer group, and paste them there.

### 4. Add Nodes to the Failover Queue

Switch to the transfer group, select the nodes that should participate in failover, then use either of these actions:

- Press `Enter`.
- Right-click and choose "添加到故障队列" (Add to Failover Queue).

After the nodes are added, they enter the failover queue and display priority labels such as `P1` and `P2`.

### 5. Adjust Priority

The failover queue order from top to bottom is the priority order:

- The top node has the highest priority.
- Lower nodes are later failover candidates.
- If the order is not what you want, drag nodes up or down manually.
- Multi-selection drag is supported. Selected nodes move together as one continuous block.

### 6. Enable Failover

After the active failover group and failover queue are ready, click the "故障转移" (Failover) button at the bottom to enable failover.

Before enabling it, make sure:

- An active failover group has been set.
- The failover queue contains at least one valid node.
- The queue order matches your intended priority.

## How to Use Drag-and-Drop Sorting

### Node Dragging

In the node list, hold and drag a node row to adjust its display order.

Inside a transfer group, node dragging also changes failover queue priority. Nodes placed higher have higher priority, and nodes placed lower have lower priority.

### Multi-Selection Node Dragging

The node list supports multi-selection dragging. Select multiple nodes and drag them together; their original relative order is preserved, and they move as one continuous block.

### Subscription Group Dragging

In the subscription group area on the main window, hold a real subscription group name and drag it to adjust group order. The virtual "全部" (All) group is not included in drag-and-drop sorting.

## Usage Notes

- Before enabling failover, create a transfer group, set it as the active failover group, and add valid nodes to the failover queue.
- Failover priority follows the visible queue order from top to bottom.
- If node drag-and-drop sorting depends on a setting, enable the related option in settings and restart the client as prompted by the UI.
- This project is a customized build based on `v2rayN 7.20.4`. General usage is close to the official version, but failover and drag-and-drop sorting behavior should follow this document.

## License

This project is based on the upstream `v2rayN` source code and follows the upstream open-source license. See [LICENSE](LICENSE) for details.
